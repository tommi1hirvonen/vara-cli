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

    private static Profile SimpleProfile(string targetRoot) =>
        new("files", targetRoot, [new Source(@"C:\irrelevant-for-fake-scanner")], null);

    [Fact]
    public void Run_throws_when_the_lock_cannot_be_acquired()
    {
        var repository = new FakeSnapshotRepository();
        var pipeline = new BackupPipeline(new FakeFileSystemScanner([]), new FakeHasher(), new FakeContentStore(), repository, new FakeRunLock(acquirable: false));

        var ex = Assert.Throws<BackupAlreadyRunningException>(() => pipeline.Run(SimpleProfile(_root)));
        Assert.Equal("files", ex.ProfileName);

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
    public void A_scan_time_failure_is_recorded_but_does_not_abort_the_run()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "hello");
        var entry = new ScannedEntry("a.txt", path, 5, File.GetLastWriteTimeUtc(path), false, null);
        var scanFailure = new ScanFailure("denied-dir", ScanFailureReason.UnreadableDirectory);
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

    private sealed class ThrowingScanner : IFileSystemScanner
    {
        public ScanResult Scan(IReadOnlyList<Source> sources) => throw new InvalidOperationException("scan failed");
    }
}
