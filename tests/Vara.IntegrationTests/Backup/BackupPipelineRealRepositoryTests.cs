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

    private static Profile SimpleProfile(string targetRoot) =>
        new("files", targetRoot, [new Source(@"C:\irrelevant-for-fake-scanner")], null);

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
}
