using System.Threading;
using Vara.Application.Backup;
using Vara.Core.Abstractions;
using Vara.Core.Backup;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.Backup;

public class BackupPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vara-test-{Guid.NewGuid():N}");

    public BackupPipelineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Profile SimpleProfile(string targetRoot, ConcurrencySettings? concurrency = null) =>
        new("files", targetRoot, [new Source(@"C:\irrelevant-for-fake-scanner")], null, concurrency);

    [Fact]
    public void Run_throws_when_the_lock_cannot_be_acquired()
    {
        var repository = new FakeSnapshotRepository();
        var pipeline = new BackupPipeline(new FakeFileSystemScanner([]), new FakeHasher(), new FakeContentStore(), repository, new FakeRunLock(acquirable: false));

        var ex = Assert.Throws<BackupAlreadyRunningException>(() => pipeline.Run(SimpleProfile(_root)));
        Assert.Equal("files", ex.ProfileName);
        Assert.Equal(_root, ex.TargetRoot);
        Assert.Contains(_root, ex.Message);
        Assert.DoesNotContain("files' is already in progress", ex.Message);

        // Nothing should have been touched if the lock was never acquired.
        Assert.Empty(repository.ListSnapshots());
    }

    [Fact]
    public void A_successful_run_completes_the_snapshot()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "hello");
        var entry = new ScannedEntry("a.txt", path, 5, File.GetLastWriteTimeUtc(path), false, null);
        var repository = new FakeSnapshotRepository();

        var pipeline = new BackupPipeline(
            new FakeFileSystemScanner([entry]), new FakeHasher(), new FakeContentStore(), repository, new FakeRunLock());

        var result = pipeline.Run(SimpleProfile(_root));

        var snapshot = Assert.Single(repository.ListSnapshots());
        Assert.Equal(SnapshotStatus.Complete, snapshot.Status);
        Assert.Equal(1, result.Stats.FilesAdded);
        Assert.Equal(1, repository.ReconcileCallCount);
    }

    [Fact]
    public void PlanOnly_throws_when_the_lock_cannot_be_acquired()
    {
        var repository = new FakeSnapshotRepository();
        var pipeline = new BackupPipeline(new FakeFileSystemScanner([]), new FakeHasher(), new FakeContentStore(), repository, new FakeRunLock(acquirable: false));

        var ex = Assert.Throws<BackupAlreadyRunningException>(() => pipeline.PlanOnly(SimpleProfile(_root)));
        Assert.Equal("files", ex.ProfileName);
        Assert.Equal(_root, ex.TargetRoot);

        // Nothing should have been touched if the lock was never acquired.
        Assert.Empty(repository.ListSnapshots());
    }

    [Fact]
    public void PlanOnly_performs_zero_writes_for_a_profile_with_pending_adds_changes_deletes_and_moves()
    {
        var unchangedPath = Path.Combine(_root, "unchanged.txt");
        File.WriteAllText(unchangedPath, "same");
        var unchangedEntry = new ScannedEntry("unchanged.txt", unchangedPath, new FileInfo(unchangedPath).Length, File.GetLastWriteTimeUtc(unchangedPath), false, null);

        var oldNamePath = Path.Combine(_root, "old-name.txt");
        File.WriteAllText(oldNamePath, "move-me");
        var oldNameEntry = new ScannedEntry("old-name.txt", oldNamePath, new FileInfo(oldNamePath).Length, File.GetLastWriteTimeUtc(oldNamePath), false, null);

        var changedPath = Path.Combine(_root, "changed.txt");
        File.WriteAllText(changedPath, "v1");
        var changedEntryV1 = new ScannedEntry("changed.txt", changedPath, new FileInfo(changedPath).Length, File.GetLastWriteTimeUtc(changedPath), false, null);

        var deletedPath = Path.Combine(_root, "deleted.txt");
        File.WriteAllText(deletedPath, "gone");
        var deletedEntry = new ScannedEntry("deleted.txt", deletedPath, new FileInfo(deletedPath).Length, File.GetLastWriteTimeUtc(deletedPath), false, null);

        var repository = new FakeSnapshotRepository();
        var contentStore = new FakeContentStore();

        new BackupPipeline(
                new FakeFileSystemScanner([unchangedEntry, oldNameEntry, changedEntryV1, deletedEntry]),
                new FakeHasher(), contentStore, repository, new FakeRunLock())
            .Run(SimpleProfile(_root));

        var mirrorBeforeDryRun = contentStore.Mirror.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var snapshotCountBeforeDryRun = repository.ListSnapshots().Count;

        // Second scan (used for the dry run): old-name.txt renamed to new-name.txt (same
        // content -> Move), changed.txt's content updated (Change), deleted.txt no longer
        // scanned (Delete), and a brand-new added.txt (Add). unchanged.txt stays as-is.
        File.WriteAllText(changedPath, "v2-longer-content");
        var changedEntryV2 = new ScannedEntry("changed.txt", changedPath, new FileInfo(changedPath).Length, File.GetLastWriteTimeUtc(changedPath), false, null);

        var newNamePath = Path.Combine(_root, "new-name.txt");
        File.WriteAllText(newNamePath, "move-me");
        var newNameEntry = new ScannedEntry("new-name.txt", newNamePath, new FileInfo(newNamePath).Length, File.GetLastWriteTimeUtc(newNamePath), false, null);

        var addedPath = Path.Combine(_root, "added.txt");
        File.WriteAllText(addedPath, "brand-new");
        var addedEntry = new ScannedEntry("added.txt", addedPath, new FileInfo(addedPath).Length, File.GetLastWriteTimeUtc(addedPath), false, null);

        var dryRunScanner = new FakeFileSystemScanner([unchangedEntry, newNameEntry, changedEntryV2, addedEntry]);
        var summary = new BackupPipeline(dryRunScanner, new FakeHasher(), contentStore, repository, new FakeRunLock())
            .PlanOnly(SimpleProfile(_root));

        Assert.Equal(1, summary.FilesAdded);
        Assert.Equal(1, summary.FilesChanged);
        Assert.Equal(1, summary.FilesMoved);
        Assert.Equal(1, summary.FilesDeleted);
        Assert.Equal(addedEntry.Size + changedEntryV2.Size, summary.TotalBytesToTransfer);
        Assert.Empty(summary.FailedPaths);

        // Zero writes: the mirror, content store, and manifest (snapshot list) are
        // exactly as they were before the dry run.
        Assert.Equal(snapshotCountBeforeDryRun, repository.ListSnapshots().Count);
        Assert.Equal(mirrorBeforeDryRun.Count, contentStore.Mirror.Count);
        foreach (var (path, hash) in mirrorBeforeDryRun)
        {
            Assert.True(contentStore.Mirror.TryGetValue(path, out var actualHash));
            Assert.Equal(hash, actualHash);
        }
    }

    [Fact]
    public void PlanOnlys_reported_counts_match_what_a_real_Run_against_the_same_state_would_report()
    {
        var basePath = Path.Combine(_root, "base.txt");
        File.WriteAllText(basePath, "hello");
        var baseEntry = new ScannedEntry("base.txt", basePath, new FileInfo(basePath).Length, File.GetLastWriteTimeUtc(basePath), false, null);

        var repository = new FakeSnapshotRepository();
        var contentStore = new FakeContentStore();
        new BackupPipeline(new FakeFileSystemScanner([baseEntry]), new FakeHasher(), contentStore, repository, new FakeRunLock())
            .Run(SimpleProfile(_root));

        File.WriteAllText(basePath, "hello, changed");
        var changedBaseEntry = new ScannedEntry("base.txt", basePath, new FileInfo(basePath).Length, File.GetLastWriteTimeUtc(basePath), false, null);
        var newPath = Path.Combine(_root, "new.txt");
        File.WriteAllText(newPath, "new content");
        var newEntry = new ScannedEntry("new.txt", newPath, new FileInfo(newPath).Length, File.GetLastWriteTimeUtc(newPath), false, null);
        var scanner = new FakeFileSystemScanner([changedBaseEntry, newEntry]);

        // PlanOnly performs zero writes (proven separately above), so calling it before
        // the real Run below against the same scanner/repository/content-store state is
        // safe, and lets both be compared against the exact same starting manifest state.
        var summary = new BackupPipeline(scanner, new FakeHasher(), contentStore, repository, new FakeRunLock())
            .PlanOnly(SimpleProfile(_root));

        var realRun = new BackupPipeline(scanner, new FakeHasher(), contentStore, repository, new FakeRunLock())
            .Run(SimpleProfile(_root));

        Assert.Equal(realRun.Stats.FilesAdded, summary.FilesAdded);
        Assert.Equal(realRun.Stats.FilesChanged, summary.FilesChanged);
        Assert.Equal(realRun.Stats.FilesMoved, summary.FilesMoved);
        Assert.Equal(realRun.Stats.FilesDeleted, summary.FilesDeleted);
        Assert.Equal(realRun.Stats.BytesTransferred, summary.TotalBytesToTransfer);
    }

    [Fact]
    public void PlanOnly_reports_scan_failures_as_paths_a_real_run_would_skip_without_aborting()
    {
        var scanFailure = new ScanFailure("denied-dir", ScanFailureReason.UnreadableDirectory, "denied-dir");
        var repository = new FakeSnapshotRepository();

        var summary = new BackupPipeline(
                new FakeFileSystemScanner([], [scanFailure]), new FakeHasher(), new FakeContentStore(), repository, new FakeRunLock())
            .PlanOnly(SimpleProfile(_root));

        // The scan failure is reported, not thrown - the dry run completes normally.
        Assert.Contains("denied-dir", summary.FailedPaths);
        Assert.Equal(0, summary.FilesAdded);
        Assert.Empty(repository.ListSnapshots());
    }

    [Fact]
    public void PlanOnly_reports_zero_planned_changes_for_a_profile_with_nothing_pending()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "hello");
        var entry = new ScannedEntry("a.txt", path, 5, File.GetLastWriteTimeUtc(path), false, null);

        var repository = new FakeSnapshotRepository();
        var contentStore = new FakeContentStore();
        var scanner = new FakeFileSystemScanner([entry]);

        new BackupPipeline(scanner, new FakeHasher(), contentStore, repository, new FakeRunLock()).Run(SimpleProfile(_root));

        // Second call against the same, now-unchanged state: nothing pending.
        var summary = new BackupPipeline(scanner, new FakeHasher(), contentStore, repository, new FakeRunLock())
            .PlanOnly(SimpleProfile(_root));

        Assert.Equal(0, summary.FilesAdded);
        Assert.Equal(0, summary.FilesChanged);
        Assert.Equal(0, summary.FilesMoved);
        Assert.Equal(0, summary.FilesDeleted);
        Assert.Equal(0, summary.TotalBytesToTransfer);
        Assert.Empty(summary.FailedPaths);
    }

    private static List<ScannedEntry> WriteFiles(string root, int count)
    {
        var entries = new List<ScannedEntry>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(root, $"file-{i}.txt");
            File.WriteAllText(path, $"content {i}");
            entries.Add(new ScannedEntry($"file-{i}.txt", path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), false, null));
        }

        return entries;
    }

    [Fact]
    public void Run_with_unconfigured_concurrency_leaves_the_executors_own_default_of_one_in_effect()
    {
        var entries = WriteFiles(_root, count: 12);
        var hasher = new ConcurrencyObservingHasher();
        var pipeline = new BackupPipeline(
            new FakeFileSystemScanner(entries), hasher, new FakeContentStore(), new FakeSnapshotRepository(), new FakeRunLock());

        pipeline.Run(SimpleProfile(_root));

        Assert.Equal(1, hasher.MaxObservedConcurrency);
    }

    [Fact]
    public void Run_with_configured_transfer_concurrency_passes_it_through_to_the_executor()
    {
        var entries = WriteFiles(_root, count: 12);
        var hasher = new ConcurrencyObservingHasher();
        var pipeline = new BackupPipeline(
            new FakeFileSystemScanner(entries), hasher, new FakeContentStore(), new FakeSnapshotRepository(), new FakeRunLock());
        var profile = SimpleProfile(_root, new ConcurrencySettings(scanConcurrency: null, transferConcurrency: 8));

        pipeline.Run(profile);

        Assert.True(hasher.MaxObservedConcurrency > 1, $"expected more than one concurrent transfer, observed {hasher.MaxObservedConcurrency}");
    }

    [Fact]
    public void Concurrent_chunk_level_progress_reports_never_lose_an_update_under_configured_concurrency()
    {
        var entries = WriteFiles(_root, count: 40);
        var expectedTotal = entries.Sum(e => e.Size);
        var hasher = new ConcurrencyObservingHasher();
        var pipeline = new BackupPipeline(
            new FakeFileSystemScanner(entries), hasher, new FakeContentStore(), new FakeSnapshotRepository(), new FakeRunLock());
        var profile = SimpleProfile(_root, new ConcurrencySettings(scanConcurrency: null, transferConcurrency: 8));
        var progress = new SyncProgress<BackupProgress>();

        pipeline.Run(profile, progress);

        // Every file's transfer reports chunk-level progress concurrently (transfer_concurrency
        // 8); an unsynchronized bytesSoFar accumulation could silently drop updates under this
        // level of concurrency. The highest reported cumulative total must still equal the sum
        // of every file's size - no update lost.
        var maxReported = progress.Reports.Max(r => r.BytesTransferred);
        Assert.Equal(expectedTotal, maxReported);
    }

    [Fact]
    public void Progress_never_exceeds_100_percent_when_the_target_has_no_hardlink_support()
    {
        var entries = WriteFiles(_root, count: 10);
        var contentStore = new FakeContentStore();
        contentStore.ForceHardlinkSupportForTesting(false);
        var pipeline = new BackupPipeline(
            new FakeFileSystemScanner(entries), new FakeHasher(), contentStore, new FakeSnapshotRepository(), new FakeRunLock());
        var progress = new SyncProgress<BackupProgress>();

        pipeline.Run(SimpleProfile(_root), progress);

        // Every file's placement falls back to a real streamed copy (no hardlink support),
        // so its bytes are reported twice (source read + mirror placement). The progress
        // denominator must account for that upfront so the percentage stays bounded.
        Assert.NotEmpty(progress.Reports);
        Assert.All(progress.Reports, r => Assert.True(
            r.BytesTransferred <= r.TotalBytes, $"progress {r.BytesTransferred}/{r.TotalBytes} exceeded 100%"));
    }

    [Fact]
    public void The_first_progress_report_is_deferred_until_after_move_delete_operations_complete()
    {
        var content = "identical content, relocated";
        var oldPath = Path.Combine(_root, "old-location.txt");
        File.WriteAllText(oldPath, content);
        var oldEntry = new ScannedEntry("old.txt", oldPath, new FileInfo(oldPath).Length, File.GetLastWriteTimeUtc(oldPath), false, null);

        var repository = new FakeSnapshotRepository();
        var contentStore = new FakeContentStore();
        new BackupPipeline(new FakeFileSystemScanner([oldEntry]), new FakeHasher(), contentStore, repository, new FakeRunLock())
            .Run(SimpleProfile(_root));

        // Second run: the same content is now scanned under a different relative path,
        // and the old path is no longer scanned - BackupPlanner detects this as a Move
        // (matching content hash) rather than an Add/Delete pair, producing a plan with
        // a Move operation and zero byte-transferring operations.
        var newPath = Path.Combine(_root, "new-location.txt");
        File.WriteAllText(newPath, content);
        var newEntry = new ScannedEntry("new.txt", newPath, new FileInfo(newPath).Length, File.GetLastWriteTimeUtc(newPath), false, null);

        var reportObserved = false;
        var moveAlreadyAppliedWhenReportArrived = false;
        var progress = new CallbackProgress<BackupProgress>(_ =>
        {
            reportObserved = true;
            moveAlreadyAppliedWhenReportArrived =
                contentStore.Mirror.ContainsKey("new.txt") && !contentStore.Mirror.ContainsKey("old.txt");
        });

        var secondRun = new BackupPipeline(new FakeFileSystemScanner([newEntry]), new FakeHasher(), contentStore, repository, new FakeRunLock())
            .Run(SimpleProfile(_root), progress);

        Assert.Equal(1, secondRun.Stats.FilesMoved);
        Assert.True(reportObserved, "expected at least one progress report");
        Assert.True(
            moveAlreadyAppliedWhenReportArrived,
            "expected the move to already be reflected in the mirror by the time the first progress report arrived");
    }

    [Fact]
    public void A_run_with_nothing_to_transfer_still_reports_zero_over_zero_progress()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "hello");
        var entry = new ScannedEntry("a.txt", path, 5, File.GetLastWriteTimeUtc(path), false, null);

        var repository = new FakeSnapshotRepository();
        var contentStore = new FakeContentStore();
        var scanner = new FakeFileSystemScanner([entry]);

        new BackupPipeline(scanner, new FakeHasher(), contentStore, repository, new FakeRunLock()).Run(SimpleProfile(_root));

        // Second run: same file, unchanged - an empty plan (no Move/Delete, no Add/Change).
        var progress = new SyncProgress<BackupProgress>();
        var secondRun = new BackupPipeline(scanner, new FakeHasher(), contentStore, repository, new FakeRunLock())
            .Run(SimpleProfile(_root), progress);

        Assert.Equal(0, secondRun.Stats.BytesTransferred);
        var report = Assert.Single(progress.Reports);
        Assert.Equal(0, report.BytesTransferred);
        Assert.Equal(0, report.TotalBytes);
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> invoking an arbitrary callback on the
    /// reporting thread directly, so a test can observe side effects (e.g. content-store
    /// state) exactly as they stand at the moment a given report arrives.</summary>
    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> - invokes the callback on the reporting
    /// thread directly instead of <see cref="Progress{T}"/>'s SynchronizationContext-posted,
    /// deferred delivery, so a test can assert against every report immediately after Run
    /// returns. Thread-safe, since concurrent transfers report concurrently.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly object _sync = new();
        public List<T> Reports { get; } = [];

        public void Report(T value)
        {
            lock (_sync)
            {
                Reports.Add(value);
            }
        }
    }

    [Fact]
    public void A_scan_time_failure_is_recorded_but_does_not_abort_the_run()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "hello");
        var entry = new ScannedEntry("a.txt", path, 5, File.GetLastWriteTimeUtc(path), false, null);
        var scanFailure = new ScanFailure("denied-dir", ScanFailureReason.UnreadableDirectory, "denied-dir");
        var repository = new FakeSnapshotRepository();

        var pipeline = new BackupPipeline(
            new FakeFileSystemScanner([entry], [scanFailure]), new FakeHasher(), new FakeContentStore(), repository, new FakeRunLock());

        var result = pipeline.Run(SimpleProfile(_root));

        // The run still completes and commits a snapshot rather than failing it ...
        var snapshot = Assert.Single(repository.ListSnapshots());
        Assert.Equal(SnapshotStatus.Complete, snapshot.Status);
        Assert.Equal(1, result.Stats.FilesAdded);

        // ... but the scan-time failure is folded into the same failure
        // reporting as per-file transfer failures.
        Assert.Equal(1, result.Stats.FilesFailed);
        Assert.Contains("denied-dir", result.FailedPaths);
    }

    [Fact]
    public void A_failure_during_the_run_marks_the_snapshot_failed_and_rethrows()
    {
        var repository = new FakeSnapshotRepository();
        var throwingScanner = new ThrowingScanner();

        var pipeline = new BackupPipeline(throwingScanner, new FakeHasher(), new FakeContentStore(), repository, new FakeRunLock());

        Assert.Throws<InvalidOperationException>(() => pipeline.Run(SimpleProfile(_root)));

        var snapshot = Assert.Single(repository.ListSnapshots());
        Assert.Equal(SnapshotStatus.Failed, snapshot.Status);
    }

    [Fact]
    public void Two_consecutive_runs_over_a_source_containing_a_symlink_record_the_link_and_cause_no_false_deletion()
    {
        var filePath = Path.Combine(_root, "regular.txt");
        File.WriteAllText(filePath, "hello");
        var fileEntry = new ScannedEntry("regular.txt", filePath, 5, File.GetLastWriteTimeUtc(filePath), false, null);
        var linkEntry = new ScannedEntry("link", Path.Combine(_root, "link"), 0, DateTimeOffset.MinValue, true, @"C:\target");

        var repository = new FakeSnapshotRepository();
        var contentStore = new FakeContentStore();

        var firstRun = new BackupPipeline(
                new FakeFileSystemScanner([fileEntry, linkEntry]), new FakeHasher(), contentStore, repository, new FakeRunLock())
            .Run(SimpleProfile(_root));

        Assert.Equal(1, firstRun.Stats.FilesAdded);
        Assert.Equal(0, firstRun.Stats.FilesDeleted);
        Assert.Empty(firstRun.FailedPaths);

        // Second run: the same source is rescanned unchanged (same regular file, same
        // still-present symlink). Neither should be misclassified as deleted, and the
        // already-tracked link shouldn't be re-recorded.
        var secondRun = new BackupPipeline(
                new FakeFileSystemScanner([fileEntry, linkEntry]), new FakeHasher(), contentStore, repository, new FakeRunLock())
            .Run(SimpleProfile(_root));

        Assert.Equal(0, secondRun.Stats.FilesAdded);
        Assert.Equal(0, secondRun.Stats.FilesChanged);
        Assert.Equal(0, secondRun.Stats.FilesDeleted);
        Assert.Empty(secondRun.FailedPaths);

        var linkHistory = repository.GetFileHistory("link");
        var linkRecord = Assert.Single(linkHistory);
        Assert.Equal(FileChangeKind.Linked, linkRecord.ChangeKind);
        Assert.Equal(@"C:\target", linkRecord.ContentHash);

        var fileHistory = repository.GetFileHistory("regular.txt");
        Assert.DoesNotContain(fileHistory, r => r.ChangeKind == FileChangeKind.Deleted);
    }

    [Fact]
    public void Running_twice_against_an_unchanged_source_performs_zero_content_transfer_the_second_time()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "hello");
        var entry = new ScannedEntry("a.txt", path, 5, File.GetLastWriteTimeUtc(path), false, null);

        var repository = new FakeSnapshotRepository();
        var contentStore = new FakeContentStore();
        var scanner = new FakeFileSystemScanner([entry]);

        var firstRun = new BackupPipeline(scanner, new FakeHasher(), contentStore, repository, new FakeRunLock()).Run(SimpleProfile(_root));
        Assert.Equal(1, firstRun.Stats.FilesAdded);
        Assert.Equal(5, firstRun.Stats.BytesTransferred);

        // Second run: same file, unchanged. Must use a fresh lock instance (a run lock
        // is scoped to one run's process lifetime), but the same repository/content
        // store, representing persisted state from the first run.
        var secondRun = new BackupPipeline(scanner, new FakeHasher(), contentStore, repository, new FakeRunLock()).Run(SimpleProfile(_root));

        Assert.Equal(0, secondRun.Stats.FilesAdded);
        Assert.Equal(0, secondRun.Stats.FilesChanged);
        Assert.Equal(0, secondRun.Stats.BytesTransferred);
        Assert.Empty(secondRun.FailedPaths);
    }

    [Fact]
    public void Running_twice_after_a_source_files_casing_changes_treats_it_as_unchanged_not_as_a_new_addition()
    {
        var path = Path.Combine(_root, "Photo.JPG");
        File.WriteAllText(path, "hello");
        var modifiedAt = File.GetLastWriteTimeUtc(path);

        var repository = new FakeSnapshotRepository();
        var contentStore = new FakeContentStore();

        var firstEntry = new ScannedEntry("Photo.JPG", path, 5, modifiedAt, false, null);
        var firstRun = new BackupPipeline(
            new FakeFileSystemScanner([firstEntry]), new FakeHasher(), contentStore, repository, new FakeRunLock()).Run(SimpleProfile(_root));
        Assert.Equal(1, firstRun.Stats.FilesAdded);

        // Simulate the source file being renamed with only its casing changed - size and
        // modified time are unaffected, so the scanner reports the same file under a
        // different-cased path. A fresh scanner/lock models the second run's process
        // lifetime, but the same repository/content store carries persisted state forward.
        var secondEntry = new ScannedEntry("photo.jpg", path, 5, modifiedAt, false, null);
        var secondRun = new BackupPipeline(
            new FakeFileSystemScanner([secondEntry]), new FakeHasher(), contentStore, repository, new FakeRunLock()).Run(SimpleProfile(_root));

        Assert.Equal(0, secondRun.Stats.FilesAdded);
        Assert.Equal(0, secondRun.Stats.FilesChanged);
        Assert.Equal(0, secondRun.Stats.FilesDeleted);
        Assert.Equal(0, secondRun.Stats.BytesTransferred);
        Assert.Empty(secondRun.FailedPaths);

        // The prior entry is matched, not orphaned: exactly one current-state entry exists,
        // reachable under either casing.
        var current = repository.GetCurrentState();
        Assert.Single(current);
        Assert.True(current.ContainsKey("photo.jpg"));
    }

    [Fact]
    public void A_secondary_failure_while_recording_the_original_failure_does_not_replace_it()
    {
        var repository = new FakeSnapshotRepository();
        var pipeline = new BackupPipeline(new ThrowingScanner(), new FakeHasher(), new FakeContentStore(), repository, new FakeRunLock());

        // The catch block's own manifestBatch.Commit() call - made while recording the
        // run's Failed status - is forced to throw a second, unrelated exception here.
        repository.ThrowOnNextCommit = new InvalidOperationException("commit failed while recording failure");

        // The original scan failure must still be what propagates, not the secondary
        // commit failure encountered while trying to record it.
        var ex = Assert.Throws<InvalidOperationException>(() => pipeline.Run(SimpleProfile(_root)));
        Assert.Equal("scan failed", ex.Message);
    }

    [Fact]
    public void A_secondary_failure_while_recording_the_original_failure_is_still_surfaced_via_diagnostics()
    {
        var repository = new FakeSnapshotRepository();
        var diagnostics = new StringWriter();
        var pipeline = new BackupPipeline(new ThrowingScanner(), new FakeHasher(), new FakeContentStore(), repository, new FakeRunLock(), diagnostics);

        repository.ThrowOnNextCommit = new InvalidOperationException("commit failed while recording failure");

        Assert.Throws<InvalidOperationException>(() => pipeline.Run(SimpleProfile(_root)));

        // The secondary exception isn't silently discarded - it's still observable
        // through the diagnostics channel, even though it never becomes the exception
        // the caller sees.
        Assert.Contains("commit failed while recording failure", diagnostics.ToString());
    }

    [Fact]
    public void Cancellation_requested_before_execution_starts_still_records_a_Cancelled_snapshot()
    {
        // Models Ctrl+C pressed while still scanning/diffing - before any file
        // operation has started (backup-execution's "Ctrl+C pressed before any file
        // has been transferred" scenario).
        var repository = new FakeSnapshotRepository();
        var pipeline = new BackupPipeline(new FakeFileSystemScanner([]), new FakeHasher(), new FakeContentStore(), repository, new FakeRunLock());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = pipeline.Run(SimpleProfile(_root), cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        var snapshot = Assert.Single(repository.ListSnapshots());
        Assert.Equal(SnapshotStatus.Cancelled, snapshot.Status);
    }

    [Fact]
    public void A_graceful_Ctrl_C_cancellation_stops_after_the_current_operation_and_records_the_snapshot_as_Cancelled()
    {
        var pathA = Path.Combine(_root, "a.txt");
        var pathB = Path.Combine(_root, "b.txt");
        File.WriteAllText(pathA, "hello");
        File.WriteAllText(pathB, "world");
        var entryA = new ScannedEntry("a.txt", pathA, 5, File.GetLastWriteTimeUtc(pathA), false, null);
        var entryB = new ScannedEntry("b.txt", pathB, 5, File.GetLastWriteTimeUtc(pathB), false, null);

        var repository = new FakeSnapshotRepository();
        var contentStore = new FakeContentStore();

        // First run: both files tracked normally (no cancellation).
        new BackupPipeline(new FakeFileSystemScanner([entryA, entryB]), new FakeHasher(), contentStore, repository, new FakeRunLock())
            .Run(SimpleProfile(_root));

        // Second run: both files removed from source, so the diff produces two Delete
        // operations, processed sequentially via the executor's Move/Delete/Link loop -
        // deterministic, unlike the parallel Add/Change transfer loop. Cancellation is
        // requested as soon as the first delete's manifest row is recorded, modeling a
        // Ctrl+C landing right after that operation finishes but before the next starts.
        using var cts = new CancellationTokenSource();
        repository.OnRecordFileVersion = () => cts.Cancel();

        var secondRun = new BackupPipeline(new FakeFileSystemScanner([]), new FakeHasher(), contentStore, repository, new FakeRunLock())
            .Run(SimpleProfile(_root), cancellationToken: cts.Token);

        Assert.True(secondRun.Cancelled);
        Assert.Equal(1, secondRun.Stats.FilesDeleted);
        var snapshot = repository.ListSnapshots().OrderByDescending(s => s.Id).First();
        Assert.Equal(SnapshotStatus.Cancelled, snapshot.Status);
    }

    private sealed class ThrowingScanner : IFileSystemScanner
    {
        public ScanResult Scan(IReadOnlyList<Source> sources) => throw new InvalidOperationException("scan failed");
    }
}
