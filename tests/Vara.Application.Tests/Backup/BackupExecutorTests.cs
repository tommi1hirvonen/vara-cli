using Vara.Application.Backup;
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

    [Fact]
    public void Executing_an_add_stores_content_places_it_in_the_mirror_and_records_it()
    {
        var path = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, path, 11, DateTimeOffset.UtcNow, null)], 11);
        var executor = new BackupExecutor(_contentStore, _repository, _hasher);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesAdded);
        Assert.Equal(11, outcome.BytesTransferred);
        Assert.Empty(outcome.FailedPaths);
        Assert.True(_contentStore.Mirror.ContainsKey("new.txt"));
    }

    [Fact]
    public void Executing_an_add_records_a_quick_hash_for_the_stored_content()
    {
        var path = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, path, 11, DateTimeOffset.UtcNow, null)], 11);
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
            [new PlannedOperation(PlannedOperationKind.Add, "over-linked.txt", null, path, 41, DateTimeOffset.UtcNow, null)], 41);
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
                new PlannedOperation(PlannedOperationKind.Add, "locked.txt", null, lockedPath, 15, DateTimeOffset.UtcNow, null),
                new PlannedOperation(PlannedOperationKind.Add, "ok.txt", null, okPath, 17, DateTimeOffset.UtcNow, null),
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
    public void A_locked_mirror_entry_during_move_is_recorded_as_failed_without_aborting_the_run()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", @"Downloads\report.pdf");
        _contentStore.ThrowOnMove = new IOException("The process cannot access the file because it is being used by another process.");
        var okPath = WriteFile("ok.txt", "this one is fine");

        var plan = new BackupPlan(
            [
                new PlannedOperation(PlannedOperationKind.Move, @"Documents\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow, "hash-1"),
                new PlannedOperation(PlannedOperationKind.Add, "ok.txt", null, okPath, 17, DateTimeOffset.UtcNow, null),
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
                new PlannedOperation(PlannedOperationKind.Add, "ok.txt", null, okPath, 17, DateTimeOffset.UtcNow, null),
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
    public void A_failed_move_does_not_record_any_file_version_for_the_affected_paths()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", @"Downloads\report.pdf");
        _contentStore.ThrowOnMove = new IOException("locked");
        var okPath = WriteFile("ok.txt", "this one is fine");

        var plan = new BackupPlan(
            [
                new PlannedOperation(PlannedOperationKind.Move, @"Documents\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow, "hash-1"),
                new PlannedOperation(PlannedOperationKind.Add, "ok.txt", null, okPath, 17, DateTimeOffset.UtcNow, null),
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
            operations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, DateTimeOffset.UtcNow, null));
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
            operations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, DateTimeOffset.UtcNow, null));
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
            operations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, DateTimeOffset.UtcNow, null));
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
            operations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, DateTimeOffset.UtcNow, null));
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
    public void A_crash_before_batch_commit_leaves_mirror_correct_but_manifest_rows_absent()
    {
        var path = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, path, 11, DateTimeOffset.UtcNow, null)], 11);
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
}
