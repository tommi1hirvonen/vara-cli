using System.Threading;
using Vara.Application.Backup;
using Vara.Core.Abstractions;
using Xunit;

namespace Vara.Application.Tests.Backup;

public class BackupExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vara-test-{Guid.NewGuid():N}");
    private readonly FakeContentStore _contentStore = new();
    private readonly FakeSnapshotRepository _repository = new();
    private readonly FakeHasher _hasher = new();

    public BackupExecutorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    // The executor now re-stats the source file after transfer and compares against the
    // scan-time values (detect-torn-reads-after-transfer change), so tests for a file that
    // is expected to transfer successfully must supply its actual on-disk modification time
    // here rather than an arbitrary DateTimeOffset.UtcNow, or the post-transfer check would
    // (correctly) treat it as modified-during-transfer and fail the operation.
    private static DateTimeOffset ScanTimeModifiedAt(string path) => new FileInfo(path).LastWriteTimeUtc;

    [Fact]
    public void Executing_an_add_stores_content_places_it_in_the_mirror_and_records_it()
    {
        var path = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, path, 11, ScanTimeModifiedAt(path), null)], 11);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesAdded);
        Assert.Equal(11, outcome.BytesTransferred);
        Assert.Empty(outcome.FailedPaths);
        Assert.True(_contentStore.Mirror.ContainsKey("new.txt"));
    }

    [Fact]
    public void A_single_large_file_transfer_reports_progress_incrementally_not_just_once_on_completion()
    {
        var content = new string('a', 500_000);
        var path = WriteFile("large.txt", content);
        var size = new FileInfo(path).Length;
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "large.txt", null, path, size, ScanTimeModifiedAt(path), null)], size);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var reports = new List<long>();
        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, reports.Add);

        Assert.True(reports.Count > 1, $"expected more than one progress report for a large file, observed {reports.Count}");
        Assert.Equal(size, reports.Sum());
    }

    [Fact]
    public void Executing_an_add_records_a_quick_hash_for_the_stored_content()
    {
        var path = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, path, 11, ScanTimeModifiedAt(path), null)], 11);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        var record = Assert.Single(_repository.GetFileHistory("new.txt"));
        Assert.NotNull(record.QuickHash);
        Assert.Equal(Vara.Core.Hashing.QuickHashPolicy.CurrentScheme, record.QuickHashScheme);
    }

    [Fact]
    public void A_placement_that_succeeds_via_a_per_blob_copy_fallback_is_recorded_like_any_other_success()
    {
        // BackupExecutor only ever sees IContentStore.PlaceAtMirrorPath succeed or throw - it
        // has no knowledge of whether the real FileSystemContentStore placed the blob via a
        // hardlink or fell back to a real copy because that blob's hard-link limit was reached
        // (fix-hardlink-limit-fallback). FakeContentStore.PlaceAtMirrorPath never throws, which
        // stands in for that successful-outcome case regardless of mechanism: confirms the
        // executor records the file normally and does not treat it as failed.
        var path = WriteFile("over-linked.txt", "content whose blob hit the hard-link cap");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "over-linked.txt", null, path, 40, ScanTimeModifiedAt(path), null)], 40);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesAdded);
        Assert.Empty(outcome.FailedPaths);
        Assert.True(_contentStore.Mirror.ContainsKey("over-linked.txt"));
        Assert.Single(_repository.GetFileHistory("over-linked.txt"));
    }

    [Fact]
    public void A_locked_source_file_is_recorded_as_failed_without_aborting_the_run()
    {
        var lockedPath = WriteFile("locked.txt", "cannot read me");
        var okPath = WriteFile("ok.txt", "this one is fine");
        using var exclusiveHold = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var plan = new BackupPlan(
            [
                new PlannedOperation(PlannedOperationKind.Add, "locked.txt", null, lockedPath, 15, ScanTimeModifiedAt(lockedPath), null),
                new PlannedOperation(PlannedOperationKind.Add, "ok.txt", null, okPath, new FileInfo(okPath).Length, ScanTimeModifiedAt(okPath), null),
            ],
            32);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesFailed);
        Assert.Equal(["locked.txt"], outcome.FailedPaths);
        Assert.Equal(1, outcome.FilesAdded);
        Assert.True(_contentStore.Mirror.ContainsKey("ok.txt"));
        Assert.False(_contentStore.Mirror.ContainsKey("locked.txt"));
    }

    [Fact]
    public void Executing_a_move_relocates_the_mirror_entry_and_records_delete_plus_move()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", @"Downloads\report.pdf");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Move, @"Documents\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow, "hash-1")],
            0);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesMoved);
        Assert.Equal(0, outcome.BytesTransferred);
        Assert.False(_contentStore.Mirror.ContainsKey(@"Downloads\report.pdf"));
        Assert.True(_contentStore.Mirror.ContainsKey(@"Documents\report.pdf"));

        var history = _repository.GetFileHistory(@"Downloads\report.pdf");
        Assert.Contains(history, r => r.ChangeKind == Vara.Core.Snapshots.FileChangeKind.Deleted);
    }

    [Fact]
    public void Executing_a_move_carries_the_matched_candidates_quick_hash_forward_without_rehashing()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", @"Downloads\report.pdf");
        var plan = new BackupPlan(
            [new PlannedOperation(
                PlannedOperationKind.Move, @"Documents\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow,
                "hash-1", QuickHash: "quick-1", QuickHashScheme: 1)],
            0);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        var newPathRecord = _repository.GetFileHistory(@"Documents\report.pdf")
            .Single(r => r.ChangeKind == Vara.Core.Snapshots.FileChangeKind.Moved);
        Assert.Equal("quick-1", newPathRecord.QuickHash);
        Assert.Equal(1, newPathRecord.QuickHashScheme);
    }

    [Fact]
    public void A_move_retried_after_the_mirror_was_already_relocated_completes_normally()
    {
        // Simulates a run interrupted between the mirror's physical relocation and the
        // manifest transaction recording it: the destination already holds the content
        // and the source is already gone, but no manifest row exists yet for either path.
        _contentStore.PlaceAtMirrorPath("hash-1", @"Documents\report.pdf");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Move, @"Documents\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow, "hash-1")],
            0);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesMoved);
        Assert.Empty(outcome.FailedPaths);
        Assert.True(_contentStore.Mirror.ContainsKey(@"Documents\report.pdf"));
        Assert.Contains(
            _repository.GetFileHistory(@"Downloads\report.pdf"),
            r => r.ChangeKind == Vara.Core.Snapshots.FileChangeKind.Deleted);
        Assert.Contains(
            _repository.GetFileHistory(@"Documents\report.pdf"),
            r => r.ChangeKind == Vara.Core.Snapshots.FileChangeKind.Moved);
    }

    [Fact]
    public void Executing_a_delete_removes_the_mirror_entry_and_records_a_tombstone()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", "gone.txt");
        var plan = new BackupPlan([new PlannedOperation(PlannedOperationKind.Delete, "gone.txt", null, null, 10, DateTimeOffset.UtcNow, "hash-1")], 0);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesDeleted);
        Assert.False(_contentStore.Mirror.ContainsKey("gone.txt"));
    }

    [Fact]
    public void Executing_a_delete_passes_the_operations_known_hash_to_RemoveFromMirror()
    {
        // The content store needs the deleted file's hash to restore read-only protection
        // on its blob after removing the mirror entry (protect-hardlinked-mirror-files
        // change's design.md).
        _contentStore.PlaceAtMirrorPath("hash-1", "gone.txt");
        var plan = new BackupPlan([new PlannedOperation(PlannedOperationKind.Delete, "gone.txt", null, null, 10, DateTimeOffset.UtcNow, "hash-1")], 0);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal("hash-1", _contentStore.RemoveFromMirrorHashesSeen["gone.txt"]);
    }

    [Fact]
    public void Executing_a_change_passes_the_operations_previous_content_hash_to_PlaceAtMirrorPath()
    {
        // The content store needs the previous content's hash to restore read-only
        // protection on that content's blob after overwriting the mirror entry
        // (protect-hardlinked-mirror-files change's design.md).
        var path = WriteFile("changed.txt", "new content");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Change, "changed.txt", null, path, 11, ScanTimeModifiedAt(path), null, PreviousContentHash: "old-hash")],
            11);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal("old-hash", _contentStore.PreviousContentHashesSeen["changed.txt"]);
    }

    [Fact]
    public void The_transfer_phase_starting_callback_fires_once_after_moves_deletes_and_before_any_transfer()
    {
        // A move and a delete both precede the add in the plan's operation list, but
        // Execute always runs every Move/Delete before any Add/Change regardless of
        // list order (fix-transfer-clock-start change), so the callback fired between
        // those two passes should observe both the move and the delete already applied,
        // and the add's content not yet stored.
        _contentStore.PlaceAtMirrorPath("hash-1", @"Downloads\report.pdf");
        _contentStore.PlaceAtMirrorPath("hash-2", "gone.txt");
        var addPath = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan(
            [
                new PlannedOperation(PlannedOperationKind.Move, @"Documents\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow, "hash-1"),
                new PlannedOperation(PlannedOperationKind.Delete, "gone.txt", null, null, 10, DateTimeOffset.UtcNow, "hash-2"),
                new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, addPath, 11, ScanTimeModifiedAt(addPath), null),
            ],
            11);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var callCount = 0;
        var moveAlreadyAppliedWhenCallbackFired = false;
        var deleteAlreadyAppliedWhenCallbackFired = false;
        var addNotYetStoredWhenCallbackFired = false;

        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, onTransferPhaseStarting: () =>
        {
            callCount++;
            moveAlreadyAppliedWhenCallbackFired = _contentStore.Mirror.ContainsKey(@"Documents\report.pdf");
            deleteAlreadyAppliedWhenCallbackFired = !_contentStore.Mirror.ContainsKey("gone.txt");
            addNotYetStoredWhenCallbackFired = !_contentStore.Mirror.ContainsKey("new.txt");
        });

        Assert.Equal(1, callCount);
        Assert.True(moveAlreadyAppliedWhenCallbackFired, "expected the move to already be applied when the callback fired");
        Assert.True(deleteAlreadyAppliedWhenCallbackFired, "expected the delete to already be applied when the callback fired");
        Assert.True(addNotYetStoredWhenCallbackFired, "expected the add's transfer to not have started yet when the callback fired");
    }

    [Fact]
    public void The_transfer_phase_starting_callback_fires_even_with_no_move_delete_operations()
    {
        var addPath = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan([new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, addPath, 11, ScanTimeModifiedAt(addPath), null)], 11);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var callCount = 0;
        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, onTransferPhaseStarting: () => callCount++);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void The_transfer_phase_starting_callback_fires_even_with_no_add_change_operations()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", "gone.txt");
        var plan = new BackupPlan([new PlannedOperation(PlannedOperationKind.Delete, "gone.txt", null, null, 10, DateTimeOffset.UtcNow, "hash-1")], 0);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var callCount = 0;
        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, onTransferPhaseStarting: () => callCount++);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void The_transfer_phase_starting_callback_fires_even_when_the_plan_is_entirely_empty()
    {
        var plan = new BackupPlan([], 0);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var callCount = 0;
        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, onTransferPhaseStarting: () => callCount++);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void The_transfer_phase_starting_callback_still_fires_after_a_move_operation_fails()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", @"Downloads\report.pdf");
        _contentStore.ThrowOnMove = new IOException("locked");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Move, @"Documents\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow, "hash-1")],
            0);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var callCount = 0;
        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, onTransferPhaseStarting: () => callCount++);

        Assert.Equal(1, callCount);
        Assert.Equal(1, outcome.FilesFailed);
    }

    [Fact]
    public void A_locked_mirror_entry_during_move_is_recorded_as_failed_without_aborting_the_run()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", @"Downloads\report.pdf");
        _contentStore.ThrowOnMove = new IOException("The process cannot access the file because it is being used by another process.");
        var okPath = WriteFile("ok.txt", "this one is fine");

        var plan = new BackupPlan(
            [
                new PlannedOperation(PlannedOperationKind.Move, @"Documents\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow, "hash-1"),
                new PlannedOperation(PlannedOperationKind.Add, "ok.txt", null, okPath, new FileInfo(okPath).Length, ScanTimeModifiedAt(okPath), null),
            ],
            17);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesFailed);
        Assert.Equal([@"Documents\report.pdf"], outcome.FailedPaths);
        Assert.Equal(0, outcome.FilesMoved);
        Assert.Equal(1, outcome.FilesAdded);
        Assert.True(_contentStore.Mirror.ContainsKey("ok.txt"));
    }

    [Fact]
    public void A_locked_mirror_entry_during_delete_is_recorded_as_failed_without_aborting_the_run()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", "gone.txt");
        _contentStore.ThrowOnRemove = new IOException("The process cannot access the file because it is being used by another process.");
        var okPath = WriteFile("ok.txt", "this one is fine");

        var plan = new BackupPlan(
            [
                new PlannedOperation(PlannedOperationKind.Delete, "gone.txt", null, null, 10, DateTimeOffset.UtcNow, "hash-1"),
                new PlannedOperation(PlannedOperationKind.Add, "ok.txt", null, okPath, new FileInfo(okPath).Length, ScanTimeModifiedAt(okPath), null),
            ],
            17);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesFailed);
        Assert.Equal(["gone.txt"], outcome.FailedPaths);
        Assert.Equal(0, outcome.FilesDeleted);
        Assert.Equal(1, outcome.FilesAdded);
        Assert.True(_contentStore.Mirror.ContainsKey("ok.txt"));
    }

    [Fact]
    public void A_mirror_path_escaping_the_target_root_is_recorded_as_failed_without_aborting_the_run()
    {
        // MirrorPathEscapesTargetRootException is an IOException subclass thrown by
        // FileSystemContentStore's containment guard (for example, for an unmapped UNC source
        // path). This confirms BackupExecutor's existing per-operation
        // catch (IOException or UnauthorizedAccessException) already handles it - for all three
        // mirror-write entry points - with no code change to BackupExecutor itself.
        _contentStore.PlaceAtMirrorPath("hash-move", @"Downloads\report.pdf");
        _contentStore.PlaceAtMirrorPath("hash-delete", "gone.txt");
        _contentStore.ThrowOnPlace = new MirrorPathEscapesTargetRootException(@"\\srv\share\new.txt", @"\\srv\share\new.txt", _root);
        _contentStore.ThrowOnPlaceForPath = @"\\srv\share\new.txt";
        var escapingAddPath = WriteFile("new.txt", "escapes on place");
        var okPath = WriteFile("ok.txt", "this one is fine");

        var plan = new BackupPlan(
            [
                new PlannedOperation(PlannedOperationKind.Add, @"\\srv\share\new.txt", null, escapingAddPath, 17, DateTimeOffset.UtcNow, null),
                new PlannedOperation(PlannedOperationKind.Add, "ok.txt", null, okPath, new FileInfo(okPath).Length, ScanTimeModifiedAt(okPath), null),
            ],
            34);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesFailed);
        Assert.Equal([@"\\srv\share\new.txt"], outcome.FailedPaths);
        Assert.Equal(1, outcome.FilesAdded);
        Assert.True(_contentStore.Mirror.ContainsKey("ok.txt"));

        // Same exception type, verified against Move and Delete too, in separate runs so each
        // operation's failure is attributed unambiguously.
        _contentStore.ThrowOnPlace = null;
        _contentStore.ThrowOnMove = new MirrorPathEscapesTargetRootException(@"\\srv\share\report.pdf", @"\\srv\share\report.pdf", _root);
        var movePlan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Move, @"\\srv\share\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow, "hash-move")],
            0);
        var moveOutcome = executor.Execute(_repository.BeginSnapshot(DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, movePlan);
        Assert.Equal(1, moveOutcome.FilesFailed);
        Assert.Equal([@"\\srv\share\report.pdf"], moveOutcome.FailedPaths);

        _contentStore.ThrowOnMove = null;
        _contentStore.ThrowOnRemove = new MirrorPathEscapesTargetRootException(@"\\srv\share\gone.txt", @"\\srv\share\gone.txt", _root);
        var deletePlan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Delete, "gone.txt", null, null, 10, DateTimeOffset.UtcNow, "hash-delete")],
            0);
        var deleteOutcome = executor.Execute(_repository.BeginSnapshot(DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, deletePlan);
        Assert.Equal(1, deleteOutcome.FilesFailed);
        Assert.Equal(["gone.txt"], deleteOutcome.FailedPaths);
    }

    [Fact]
    public void A_failed_move_does_not_record_any_file_version_for_the_affected_paths()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", @"Downloads\report.pdf");
        _contentStore.ThrowOnMove = new IOException("locked");
        var okPath = WriteFile("ok.txt", "this one is fine");

        var plan = new BackupPlan(
            [
                new PlannedOperation(PlannedOperationKind.Move, @"Documents\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow, "hash-1"),
                new PlannedOperation(PlannedOperationKind.Add, "ok.txt", null, okPath, new FileInfo(okPath).Length, ScanTimeModifiedAt(okPath), null),
            ],
            17);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Empty(_repository.GetFileHistory(@"Downloads\report.pdf"));
        Assert.Empty(_repository.GetFileHistory(@"Documents\report.pdf"));
        Assert.Single(_repository.GetFileHistory("ok.txt"));
    }

    [Fact]
    public void Many_adds_execute_correctly_under_bounded_parallelism()
    {
        const int fileCount = 50;
        var operations = new List<PlannedOperation>();
        long expectedBytes = 0;

        for (var i = 0; i < fileCount; i++)
        {
            var content = $"content of file {i}";
            var path = WriteFile($"file-{i}.txt", content);
            var size = new FileInfo(path).Length;
            expectedBytes += size;
            operations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, ScanTimeModifiedAt(path), null));
        }

        var plan = new BackupPlan(operations, expectedBytes);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher, maxDegreeOfParallelism: 8);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var reportedBytes = 0L;
        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, bytes => Interlocked.Add(ref reportedBytes, bytes));

        Assert.Equal(fileCount, outcome.FilesAdded);
        Assert.Equal(expectedBytes, outcome.BytesTransferred);
        Assert.Equal(expectedBytes, reportedBytes);
        Assert.Empty(outcome.FailedPaths);
        Assert.Equal(fileCount, _contentStore.Mirror.Count);
        Assert.Equal(fileCount, _repository.GetCurrentState().Count);
    }

    [Fact]
    public void Unconfigured_transfer_concurrency_defaults_to_one_not_processor_count()
    {
        const int fileCount = 12;
        var operations = new List<PlannedOperation>();
        long expectedBytes = 0;

        for (var i = 0; i < fileCount; i++)
        {
            var content = $"content of file {i}";
            var path = WriteFile($"file-{i}.txt", content);
            var size = new FileInfo(path).Length;
            expectedBytes += size;
            operations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, ScanTimeModifiedAt(path), null));
        }

        var plan = new BackupPlan(operations, expectedBytes);
        var hasher = new ConcurrencyObservingHasher();
        // No explicit maxDegreeOfParallelism - proves the executor's own unconfigured
        // default is 1 (configure-backup-concurrency), regardless of how many
        // processors are available on the machine running this test.
        var executor = new BackupExecutor(_contentStore, _repository, hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, hasher.MaxObservedConcurrency);
    }

    [Fact]
    public void Configuring_transfer_concurrency_above_one_allows_concurrent_transfers()
    {
        const int fileCount = 12;
        var operations = new List<PlannedOperation>();
        long expectedBytes = 0;

        for (var i = 0; i < fileCount; i++)
        {
            var content = $"content of file {i}";
            var path = WriteFile($"file-{i}.txt", content);
            var size = new FileInfo(path).Length;
            expectedBytes += size;
            operations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, ScanTimeModifiedAt(path), null));
        }

        var plan = new BackupPlan(operations, expectedBytes);
        var hasher = new ConcurrencyObservingHasher();
        var executor = new BackupExecutor(_contentStore, _repository, hasher, maxDegreeOfParallelism: 8);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.True(hasher.MaxObservedConcurrency > 1, $"expected more than one concurrent transfer, observed {hasher.MaxObservedConcurrency}");
    }

    [Fact]
    public void Manifest_writes_for_many_files_commit_once_as_a_batch_not_per_file()
    {
        const int fileCount = 25;
        var operations = new List<PlannedOperation>();
        long expectedBytes = 0;

        for (var i = 0; i < fileCount; i++)
        {
            var content = $"content of file {i}";
            var path = WriteFile($"file-{i}.txt", content);
            var size = new FileInfo(path).Length;
            expectedBytes += size;
            operations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, ScanTimeModifiedAt(path), null));
        }

        var plan = new BackupPlan(operations, expectedBytes);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher, maxDegreeOfParallelism: 8);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        ExecutionOutcome outcome;
        using (var batch = _repository.BeginManifestBatch())
        {
            outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

            // Nothing is durable yet - the batch covering all 25 files has not committed.
            Assert.Empty(_repository.GetFileHistory("file-0.txt"));

            batch.Commit();
        }

        Assert.Equal(fileCount, outcome.FilesAdded);
        // One commit made every file's row durable - not one commit per file.
        Assert.Equal(1, _repository.CommitCount);
        Assert.Equal(fileCount, _repository.GetCurrentState().Count);
        for (var i = 0; i < fileCount; i++)
        {
            Assert.Single(_repository.GetFileHistory($"file-{i}.txt"));
        }
    }

    [Fact]
    public void Periodic_checkpoints_commit_multiple_times_during_one_run_when_a_manifest_batch_is_supplied()
    {
        // add-backup-checkpoints-and-cancellation's design.md "Checkpoint trigger"
        // decision: a zero interval means every completed operation is immediately due
        // for a checkpoint, so multiple commits (not one final whole-run commit) is the
        // observable difference from the pre-existing batch-manifest-writes behavior.
        const int fileCount = 5;
        var operations = new List<PlannedOperation>();
        long expectedBytes = 0;

        for (var i = 0; i < fileCount; i++)
        {
            var content = $"content of file {i}";
            var path = WriteFile($"file-{i}.txt", content);
            var size = new FileInfo(path).Length;
            expectedBytes += size;
            operations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, ScanTimeModifiedAt(path), null));
        }

        var plan = new BackupPlan(operations, expectedBytes);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher, checkpointInterval: TimeSpan.Zero);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        using var batch = _repository.BeginManifestBatch();
        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, manifestBatch: batch);

        Assert.Equal(fileCount, outcome.FilesAdded);
        Assert.True(_repository.CommitCount > 1, $"Expected more than one checkpoint commit, got {_repository.CommitCount}.");

        // Every row is already durable via the executor's own periodic checkpoints,
        // even before the caller makes its own final Commit() call.
        for (var i = 0; i < fileCount; i++)
        {
            Assert.Single(_repository.GetFileHistory($"file-{i}.txt"));
        }
    }

    [Fact]
    public void No_checkpointing_occurs_when_no_manifest_batch_is_supplied()
    {
        // Existing callers that omit manifestBatch (e.g. every other test in this file)
        // must see unchanged behavior: no checkpoint commits at all.
        var path = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, path, 11, ScanTimeModifiedAt(path), null)], 11);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher, checkpointInterval: TimeSpan.Zero);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(0, _repository.CommitCount);
    }

    [Fact]
    public void Cooperative_cancellation_lets_the_current_operation_finish_but_starts_no_further_ones()
    {
        // Three Delete operations: the executor's sequential Move/Delete/Link loop
        // processes these one at a time, so cancelling right after the first one
        // finishes is deterministic (unlike the parallel transfer loop).
        var plan = new BackupPlan(
            [
                new PlannedOperation(PlannedOperationKind.Delete, "a.txt", null, null, 0, DateTimeOffset.UtcNow, "hash-a"),
                new PlannedOperation(PlannedOperationKind.Delete, "b.txt", null, null, 0, DateTimeOffset.UtcNow, "hash-b"),
                new PlannedOperation(PlannedOperationKind.Delete, "c.txt", null, null, 0, DateTimeOffset.UtcNow, "hash-c"),
            ],
            0);

        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);
        using var cts = new CancellationTokenSource();

        // Request cancellation as soon as the first operation's manifest row has been
        // recorded, modeling a Ctrl+C landing right after "a.txt" finishes but before
        // "b.txt" starts.
        _repository.OnRecordFileVersion = () => cts.Cancel();

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, cancellationToken: cts.Token);

        Assert.Equal(1, outcome.FilesDeleted);
        Assert.Single(_repository.GetFileHistory("a.txt"));
        Assert.Empty(_repository.GetFileHistory("b.txt"));
        Assert.Empty(_repository.GetFileHistory("c.txt"));
    }

    [Fact]
    public void Cooperative_cancellation_requested_before_starting_skips_every_operation()
    {
        var path = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, path, 11, ScanTimeModifiedAt(path), null)], 11);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, cancellationToken: cts.Token);

        Assert.Equal(0, outcome.FilesAdded);
        Assert.False(_contentStore.Mirror.ContainsKey("new.txt"));
        Assert.Empty(_repository.GetFileHistory("new.txt"));
    }

    [Fact]
    public void A_crash_before_batch_commit_leaves_mirror_correct_but_manifest_rows_absent()
    {
        var path = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, path, 11, ScanTimeModifiedAt(path), null)], 11);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        using (_repository.BeginManifestBatch())
        {
            var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);
            Assert.Equal(1, outcome.FilesAdded);
            // No Commit() call here - models the process crashing mid-run, before the
            // batch's transaction would have committed.
        }

        // The mirror content is already correct, since content-store writes happen
        // outside the manifest batch...
        Assert.True(_contentStore.Mirror.ContainsKey("new.txt"));

        // ...but the manifest never durably recorded it, so the next run's incremental
        // scan will find no history for "new.txt" and simply re-add it - self-healing,
        // not data loss or corruption (backup-execution spec's batching requirement).
        Assert.Empty(_repository.GetFileHistory("new.txt"));
        Assert.Empty(_repository.GetCurrentState());
    }

    [Fact]
    public void A_file_modified_between_scan_and_transfer_is_recorded_as_failed_with_no_manifest_entry()
    {
        var path = WriteFile("torn.txt", "original content as of scan time");
        var scanTimeSize = new FileInfo(path).Length;
        var scanTimeModifiedAt = ScanTimeModifiedAt(path);
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "torn.txt", null, path, scanTimeSize, scanTimeModifiedAt, null)],
            scanTimeSize);

        // Simulate the file being edited by another process after it was scanned (its stat
        // captured above) but before its content is actually read during transfer below -
        // both its size and modification time now diverge from the scan-time values baked
        // into the plan.
        File.WriteAllText(path, "content changed after scan, before transfer completed");
        File.SetLastWriteTimeUtc(path, scanTimeModifiedAt.UtcDateTime.AddMinutes(5));

        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesFailed);
        Assert.Equal(["torn.txt"], outcome.FailedPaths);
        Assert.Equal(0, outcome.FilesAdded);
        Assert.Empty(_repository.GetFileHistory("torn.txt"));
        Assert.False(_contentStore.Mirror.ContainsKey("torn.txt"));
    }

    [Fact]
    public void A_file_modified_during_transfer_is_re_selected_by_incremental_change_detection_on_the_next_run()
    {
        // First run: a file is scanned, then edited (torn read) before its content is
        // actually read during transfer, so the operation fails and no manifest row is
        // written for it (previous test). This test confirms the natural consequence:
        // since the manifest's current state for this path is untouched, a subsequent
        // scan of the file's now-current on-disk state still differs from that recorded
        // state, so BackupDiffer selects it again.
        var path = WriteFile("torn.txt", "original content as of scan time");
        var scanTimeSize = new FileInfo(path).Length;
        var scanTimeModifiedAt = ScanTimeModifiedAt(path);
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "torn.txt", null, path, scanTimeSize, scanTimeModifiedAt, null)],
            scanTimeSize);

        File.WriteAllText(path, "content changed after scan, before transfer completed");
        File.SetLastWriteTimeUtc(path, scanTimeModifiedAt.UtcDateTime.AddMinutes(5));

        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);
        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);
        Assert.Equal(1, outcome.FilesFailed);

        // Second run: scan observes the file's current (post-edit) state; diff it against
        // the manifest's current state, which is still empty for this path since the
        // failed operation above never recorded anything.
        var currentOnDiskInfo = new FileInfo(path);
        var scannedEntry = new ScannedEntry("torn.txt", path, currentOnDiskInfo.Length, currentOnDiskInfo.LastWriteTimeUtc, IsLink: false, LinkTarget: null);
        var diffResult = new BackupDiffer().Diff([scannedEntry], _repository.GetCurrentState(), []);

        var pending = Assert.Single(diffResult.Pending);
        Assert.Equal("torn.txt", pending.Entry.RelativePath);
        Assert.Equal(PendingChangeKind.Added, pending.Kind);
    }

    [Fact]
    public void An_unmodified_during_transfer_file_still_records_its_manifest_entry_with_the_scan_time_modification_time()
    {
        var path = WriteFile("stable.txt", "content that is not touched during its own transfer");
        var scanTimeSize = new FileInfo(path).Length;
        var scanTimeModifiedAt = ScanTimeModifiedAt(path);
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "stable.txt", null, path, scanTimeSize, scanTimeModifiedAt, null)],
            scanTimeSize);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesAdded);
        Assert.Empty(outcome.FailedPaths);
        Assert.True(_contentStore.Mirror.ContainsKey("stable.txt"));
        var record = Assert.Single(_repository.GetFileHistory("stable.txt"));
        Assert.Equal(scanTimeModifiedAt, record.SourceModifiedAt);
    }

    [Fact]
    public void A_source_file_deleted_immediately_after_being_read_is_recorded_as_failed()
    {
        var path = WriteFile("vanishing.txt", "will vanish right after being read");
        var size = new FileInfo(path).Length;
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "vanishing.txt", null, path, size, ScanTimeModifiedAt(path), null)],
            size);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        // onBytesTransferred fires from within StoreFromStream, once the content has
        // already been fully read - deleting the source here simulates the re-stat call
        // itself throwing (FileNotFoundException, an IOException) rather than observing a
        // mismatch.
        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, onBytesTransferred: _ =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        });

        Assert.Equal(1, outcome.FilesFailed);
        Assert.Equal(["vanishing.txt"], outcome.FailedPaths);
        Assert.Equal(0, outcome.FilesAdded);
        Assert.Empty(_repository.GetFileHistory("vanishing.txt"));
        Assert.False(_contentStore.Mirror.ContainsKey("vanishing.txt"));
    }
}
