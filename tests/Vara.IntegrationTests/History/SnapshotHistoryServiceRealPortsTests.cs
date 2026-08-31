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
}
