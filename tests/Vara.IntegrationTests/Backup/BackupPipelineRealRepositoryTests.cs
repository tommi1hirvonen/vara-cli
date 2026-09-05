using Microsoft.Data.Sqlite;
using Vara.Application.Backup;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Infrastructure.Snapshots;
using Xunit;

namespace Vara.IntegrationTests.Backup;

/// <summary>
/// Runs <see cref="BackupPipeline"/> against the real <see cref="SqliteSnapshotRepository"/>
/// instead of a fake, per design.md's decision: this is the port whose fake counterpart most
/// recently duplicated a real bug (the <c>fix-current-state-case-sensitivity</c> change).
/// <see cref="IContentStore"/>/<see cref="IFileSystemScanner"/> remain faked here - see
/// design.md for why (hardlink-probing complexity orthogonal to this bug class).
/// </summary>
public class BackupPipelineRealRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vara-inttest-{Guid.NewGuid():N}.db");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vara-inttest-{Guid.NewGuid():N}");
    private SqliteSnapshotRepository? _repository;

    private SqliteSnapshotRepository Repository => _repository ??= new SqliteSnapshotRepository(_dbPath);

    public BackupPipelineRealRepositoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _repository?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Profile SimpleProfile(string targetRoot, ConcurrencySettings? concurrency = null) =>
        new("files", targetRoot, [new Source(@"C:\irrelevant-for-fake-scanner")], null, concurrency);

    [Fact]
    public void A_run_against_the_real_repository_records_an_added_file_in_current_state()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "hello");
        var entry = new ScannedEntry("a.txt", path, 5, File.GetLastWriteTimeUtc(path), false, null);

        var pipeline = new BackupPipeline(
            new FakeFileSystemScanner([entry]), new FakeHasher(), new FakeContentStore(), Repository, new FakeRunLock());
        var result = pipeline.Run(SimpleProfile(_root));

        Assert.Equal(1, result.Stats.FilesAdded);
        Assert.True(Repository.GetCurrentState().ContainsKey("a.txt"));
    }

    [Fact]
    public void Running_twice_against_an_unchanged_source_performs_zero_content_transfer_the_second_time()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "hello");
        var entry = new ScannedEntry("a.txt", path, 5, File.GetLastWriteTimeUtc(path), false, null);
        var contentStore = new FakeContentStore();
        var scanner = new FakeFileSystemScanner([entry]);

        var firstRun = new BackupPipeline(scanner, new FakeHasher(), contentStore, Repository, new FakeRunLock()).Run(SimpleProfile(_root));
        Assert.Equal(1, firstRun.Stats.FilesAdded);
        Assert.Equal(5, firstRun.Stats.BytesTransferred);

        // Second run: same file, unchanged. Fresh lock (scoped to one run's process
        // lifetime), same real repository/content store carrying persisted state forward.
        var secondRun = new BackupPipeline(scanner, new FakeHasher(), contentStore, Repository, new FakeRunLock()).Run(SimpleProfile(_root));

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
        var contentStore = new FakeContentStore();

        var firstEntry = new ScannedEntry("Photo.JPG", path, 5, modifiedAt, false, null);
        var firstRun = new BackupPipeline(
            new FakeFileSystemScanner([firstEntry]), new FakeHasher(), contentStore, Repository, new FakeRunLock()).Run(SimpleProfile(_root));
        Assert.Equal(1, firstRun.Stats.FilesAdded);

        // Simulate the source file being renamed with only its casing changed - size and
        // modified time are unaffected. This is the real-repository counterpart to the
        // regression added in the fix-current-state-case-sensitivity change (which only
        // exercised FakeSnapshotRepository).
        var secondEntry = new ScannedEntry("photo.jpg", path, 5, modifiedAt, false, null);
        var secondRun = new BackupPipeline(
            new FakeFileSystemScanner([secondEntry]), new FakeHasher(), contentStore, Repository, new FakeRunLock()).Run(SimpleProfile(_root));

        Assert.Equal(0, secondRun.Stats.FilesAdded);
        Assert.Equal(0, secondRun.Stats.FilesChanged);
        Assert.Equal(0, secondRun.Stats.FilesDeleted);
        Assert.Equal(0, secondRun.Stats.BytesTransferred);
        Assert.Empty(secondRun.FailedPaths);

        var current = Repository.GetCurrentState();
        Assert.Single(current);
        Assert.True(current.ContainsKey("photo.jpg"));
    }

    [Fact]
    public void A_run_with_a_configured_transfer_concurrency_above_one_still_produces_a_correct_one_to_one_mirror()
    {
        const int fileCount = 30;
        var entries = new List<ScannedEntry>();
        for (var i = 0; i < fileCount; i++)
        {
            var path = Path.Combine(_root, $"file-{i}.txt");
            var content = $"content of file {i}";
            File.WriteAllText(path, content);
            entries.Add(new ScannedEntry($"file-{i}.txt", path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), false, null));
        }

        var contentStore = new FakeContentStore();
        var profile = SimpleProfile(_root, new ConcurrencySettings(scanConcurrency: null, transferConcurrency: 8));

        var result = new BackupPipeline(
            new FakeFileSystemScanner(entries), new FakeHasher(), contentStore, Repository, new FakeRunLock()).Run(profile);

        // Functional correctness under non-default concurrency, not a timing/performance
        // assertion: every source file made it into the mirror and the current-state
        // view, with no failures, regardless of how many transfers ran concurrently.
        Assert.Equal(fileCount, result.Stats.FilesAdded);
        Assert.Empty(result.FailedPaths);
        Assert.Equal(fileCount, contentStore.Mirror.Count);
        var current = Repository.GetCurrentState();
        Assert.Equal(fileCount, current.Count);
        for (var i = 0; i < fileCount; i++)
        {
            Assert.True(current.ContainsKey($"file-{i}.txt"));
            Assert.True(contentStore.Mirror.ContainsKey($"file-{i}.txt"));
        }
    }

    [Fact]
    public void A_source_unavailable_failure_does_not_delete_that_sources_previously_mirrored_files()
    {
        var appDataPath = Path.Combine(_root, "app-data", "a.txt");
        var otherPath = Path.Combine(_root, "other.txt");
        WriteFile(appDataPath, "app data");
        WriteFile(otherPath, "other");
        var appDataEntry = new ScannedEntry(@"app-data\a.txt", appDataPath, 8, File.GetLastWriteTimeUtc(appDataPath), false, null);
        var otherEntry = new ScannedEntry("other.txt", otherPath, 5, File.GetLastWriteTimeUtc(otherPath), false, null);
        var contentStore = new FakeContentStore();

        // First run: both sources scan successfully.
        var firstRun = new BackupPipeline(
            new FakeFileSystemScanner([appDataEntry, otherEntry]), new FakeHasher(), contentStore, Repository, new FakeRunLock())
            .Run(SimpleProfile(_root));
        Assert.Equal(2, firstRun.Stats.FilesAdded);

        // Second run: the "app-data" source is now unavailable (unplugged drive/typo) - only
        // "other.txt"'s source still scans. The fake scanner reports a SourceUnavailable
        // failure whose mirror path is "app-data", matching appDataEntry's own mirror path.
        var sourceUnavailable = new ScanFailure(
            @"D:\missing-drive\app-data", ScanFailureReason.SourceUnavailable, "app-data");
        var secondRun = new BackupPipeline(
            new FakeFileSystemScanner([otherEntry], [sourceUnavailable]), new FakeHasher(), contentStore, Repository, new FakeRunLock())
            .Run(SimpleProfile(_root));

        Assert.Equal(0, secondRun.Stats.FilesDeleted);
        Assert.Equal(0, secondRun.Stats.FilesAdded);
        Assert.Equal(0, secondRun.Stats.FilesChanged);
        Assert.Contains(@"D:\missing-drive\app-data", secondRun.FailedPaths);

        var current = Repository.GetCurrentState();
        Assert.True(current.ContainsKey(@"app-data\a.txt"));
        Assert.True(current.ContainsKey("other.txt"));
        Assert.True(contentStore.Mirror.ContainsKey(@"app-data\a.txt"));
    }

    [Fact]
    public void A_permission_denied_directory_failure_does_not_delete_previously_mirrored_files_under_it()
    {
        var deniedPath = Path.Combine(_root, "denied-dir", "hidden.txt");
        var otherPath = Path.Combine(_root, "other.txt");
        WriteFile(deniedPath, "hidden");
        WriteFile(otherPath, "other");
        var deniedEntry = new ScannedEntry(@"denied-dir\hidden.txt", deniedPath, 6, File.GetLastWriteTimeUtc(deniedPath), false, null);
        var otherEntry = new ScannedEntry("other.txt", otherPath, 5, File.GetLastWriteTimeUtc(otherPath), false, null);
        var contentStore = new FakeContentStore();

        // First run: both files scan successfully.
        var firstRun = new BackupPipeline(
            new FakeFileSystemScanner([deniedEntry, otherEntry]), new FakeHasher(), contentStore, Repository, new FakeRunLock())
            .Run(SimpleProfile(_root));
        Assert.Equal(2, firstRun.Stats.FilesAdded);

        // Second run: "denied-dir" can no longer be enumerated (permission denied) - only
        // "other.txt" still scans. The fake scanner reports an UnreadableDirectory failure
        // whose mirror path is "denied-dir", the ancestor of deniedEntry's own mirror path.
        var unreadableDirectory = new ScanFailure("denied-dir", ScanFailureReason.UnreadableDirectory, "denied-dir");
        var secondRun = new BackupPipeline(
            new FakeFileSystemScanner([otherEntry], [unreadableDirectory]), new FakeHasher(), contentStore, Repository, new FakeRunLock())
            .Run(SimpleProfile(_root));

        Assert.Equal(0, secondRun.Stats.FilesDeleted);
        Assert.Contains("denied-dir", secondRun.FailedPaths);

        var current = Repository.GetCurrentState();
        Assert.True(current.ContainsKey(@"denied-dir\hidden.txt"));
        Assert.True(current.ContainsKey("other.txt"));
        Assert.True(contentStore.Mirror.ContainsKey(@"denied-dir\hidden.txt"));
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
