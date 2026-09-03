using Microsoft.Data.Sqlite;
using Vara.Application.History;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;
using Xunit;

namespace Vara.IntegrationTests.History;

/// <summary>
/// Runs <see cref="SnapshotHistoryService"/> against the real <see cref="SqliteSnapshotRepository"/>
/// AND the real <see cref="FileSystemContentStore"/>, per design.md's decision: restore's
/// mirror-containment guard (<c>IsWithinMirror</c>) and overwrite check (<c>TargetExists</c>)
/// rely on the content store's real path-resolution logic, which the fake re-implements
/// independently via separate tracked sets (<c>MirrorPaths</c>, <c>SeedExistingTarget</c>).
/// </summary>
public class SnapshotHistoryServiceRealPortsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vara-inttest-{Guid.NewGuid():N}.db");
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-inttest-{Guid.NewGuid():N}");
    private SqliteSnapshotRepository? _repository;
    private FileSystemContentStore? _contentStore;

    private SqliteSnapshotRepository Repository => _repository ??= new SqliteSnapshotRepository(_dbPath);
    private FileSystemContentStore ContentStore => _contentStore ??= new FileSystemContentStore(_targetRoot, new XxHash128Hasher());

    public void Dispose()
    {
        _repository?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    [Fact]
    public void RestoreAsOf_extracts_real_content_to_a_real_destination_file()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("old content"u8.ToArray()));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hash, 11, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(Repository, ContentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-inttest-restore-{Guid.NewGuid():N}.txt");

        try
        {
            service.RestoreAsOf("a.txt", t0.AddDays(1), destination);

            Assert.Equal("old content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreAsOf_refuses_a_destination_that_really_resolves_inside_the_live_mirror()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hash, 7, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(Repository, ContentStore);

        // The real FileSystemContentStore resolves mirror containment from the target
        // root passed to its constructor - no seeding required, unlike the fake's
        // MirrorPaths set.
        var mirrorDestination = Path.Combine(_targetRoot, "a.txt");

        var ex = Assert.Throws<RestoreDestinationInMirrorException>(
            () => service.RestoreAsOf("a.txt", t0.AddDays(1), mirrorDestination, overwrite: true));
        Assert.Equal(mirrorDestination, ex.DestinationPath);
    }

    [Fact]
    public void RestoreAsOf_to_a_real_existing_destination_without_overwrite_throws_DestinationExists()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hash, 7, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(Repository, ContentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-inttest-restore-{Guid.NewGuid():N}.txt");
        File.WriteAllText(destination, "already here");

        try
        {
            var ex = Assert.Throws<DestinationExistsException>(() => service.RestoreAsOf("a.txt", t0.AddDays(1), destination));
            Assert.Equal(destination, ex.DestinationPath);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreAsOf_to_a_real_existing_destination_with_overwrite_true_overwrites_it()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("new content"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hash, 11, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(Repository, ContentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-inttest-restore-{Guid.NewGuid():N}.txt");
        File.WriteAllText(destination, "stale content");

        try
        {
            service.RestoreAsOf("a.txt", t0.AddDays(1), destination, overwrite: true);

            Assert.Equal("new content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void ListDirectory_with_includeDeleted_reflects_a_real_backup_run_followed_by_a_real_deletion()
    {
        var (hashV1, _) = ContentStore.StoreFromStream(new MemoryStream("kept"u8.ToArray()));
        var (hashV2, _) = ContentStore.StoreFromStream(new MemoryStream("gone"u8.ToArray()));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, @"src\keep.txt", null, hashV1, 4, t0, FileChangeKind.Added, t0);
        Repository.RecordFileVersion(s1, @"src\old.txt", null, hashV2, 4, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, @"src\old.txt", null, hashV2, 4, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(Repository, ContentStore);

        var liveOnly = service.ListDirectory("src", asOf: null, includeDeleted: false);
        Assert.Single(liveOnly);

        var withDeleted = service.ListDirectory("src", asOf: null, includeDeleted: true);
        Assert.Equal(2, withDeleted.Count);
        Assert.Contains(withDeleted, e => e.Name == "old.txt" && e.Status == DirectoryEntryStatus.Deleted);
    }

    [Fact]
    public void ListDeleted_reflects_a_real_deletion_across_the_profile()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("gone"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, @"src\old.txt", null, hash, 4, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, @"src\old.txt", null, hash, 4, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(Repository, ContentStore);

        var deleted = service.ListDeleted(directoryPath: null, since: null);

        var entry = Assert.Single(deleted);
        Assert.Equal(@"src\old.txt", entry.RelativePath);
    }

    [Fact]
    public void ShowVersion_streams_real_stored_content_to_the_destination_stream()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("shown"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hash, 5, t0, FileChangeKind.Added, t0);
        var versionId = Repository.GetFileHistory("a.txt").Single().Id;
        var service = new SnapshotHistoryService(Repository, ContentStore);
        using var destination = new MemoryStream();

        service.ShowVersion("a.txt", versionId, asOf: null, destination);

        Assert.Equal("shown", System.Text.Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public void OpenVersionsForDiff_opens_real_stored_content_for_both_sides()
    {
        var (hashV1, _) = ContentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = ContentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hashV1, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = Repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(Repository, ContentStore);

        var (left, right) = service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null);
        using var leftReader = new StreamReader(left);
        using var rightReader = new StreamReader(right);

        Assert.Equal("version one", leftReader.ReadToEnd());
        Assert.Equal("version two", rightReader.ReadToEnd());
    }

    [Fact]
    public void RestoreAsOf_in_place_writes_back_to_the_original_absolute_source_path()
    {
        // Simulates restore --in-place: the CLI computes the destination via
        // AbsolutePathMirrorMapper.FromMirrorPath, then calls RestoreAsOf exactly as
        // it does for --out - this proves that reversed path is a real, writable
        // location outside the mirror that the existing guards accept.
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-inttest-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var mirrorRelativePath = Vara.Core.FileSystem.AbsolutePathMirrorMapper.ToMirrorPath(Path.Combine(sourceRoot, "deleted.txt"));
            var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("undeleted"u8.ToArray()));
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var s1 = Repository.BeginSnapshot(t0);
            Repository.RecordFileVersion(s1, mirrorRelativePath, null, hash, 9, t0, FileChangeKind.Added, t0);
            var t1 = t0.AddDays(1);
            var s2 = Repository.BeginSnapshot(t1);
            Repository.RecordFileVersion(s2, mirrorRelativePath, null, hash, 9, t1, FileChangeKind.Deleted, t1);
            var service = new SnapshotHistoryService(Repository, ContentStore);

            var originalSourcePath = Vara.Core.FileSystem.AbsolutePathMirrorMapper.FromMirrorPath(mirrorRelativePath);
            service.RestoreAsOf(mirrorRelativePath, t1.AddDays(-1), originalSourcePath);

            Assert.Equal("undeleted", File.ReadAllText(originalSourcePath));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }
}
