using Microsoft.Data.Sqlite;
using Vara.Application.Retention;
using Vara.Core.Backup;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;
using Vara.IntegrationTests.Backup;
using Vara.IntegrationTests.TestSupport;
using Xunit;

namespace Vara.IntegrationTests.Retention;

/// <summary>
/// Runs <see cref="PruneService"/> against the real <see cref="SqliteSnapshotRepository"/> AND
/// the real <see cref="FileSystemContentStore"/>, per design.md's decision: neither port's
/// hardlink surface is reachable through <see cref="PruneService"/>, so there is no flakiness
/// trade-off to weigh, and pruning/GC is a destructive operation where a real backend proves
/// the fakes' independently re-implemented "protect the current row" logic actually agrees with
/// the real repository/content store.
/// </summary>
public class PruneServiceRealPortsTests : IDisposable
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
            DirectoryCleanup.ClearReadOnlyAndDelete(_targetRoot);
        }
    }

    private static Profile ProfileWithRetention(string targetRoot, RetentionPolicy? retention) =>
        new("files", targetRoot, [new Source(@"C:\data")], retention);

    [Fact]
    public void Prune_removes_eligible_snapshots_and_garbage_collects_unreferenced_content()
    {
        var (keepHash, _) = ContentStore.StoreFromStream(new MemoryStream("kept content"u8.ToArray()));
        var (goneHash, _) = ContentStore.StoreFromStream(new MemoryStream("expired content"u8.ToArray()));

        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var day3 = day1.AddDays(2);

        // "expired.txt" is added, then deleted - both events are themselves old enough
        // to expire, so every reference to goneHash (including the deletion tombstone)
        // is eventually pruned away, making the content truly unreferenced.
        var addedSnapshot = Repository.BeginSnapshot(day1);
        Repository.RecordFileVersion(addedSnapshot, "expired.txt", null, goneHash, 10, day1, FileChangeKind.Added, day1);
        Repository.CompleteSnapshot(addedSnapshot, day1, SnapshotStats.Empty);

        var deletedSnapshot = Repository.BeginSnapshot(day2);
        Repository.RecordFileVersion(deletedSnapshot, "expired.txt", null, goneHash, 10, day2, FileChangeKind.Deleted, day2);
        Repository.CompleteSnapshot(deletedSnapshot, day2, SnapshotStats.Empty);

        var currentSnapshot = Repository.BeginSnapshot(day3);
        Repository.RecordFileVersion(currentSnapshot, "current.txt", null, keepHash, 10, day3, FileChangeKind.Added, day3);
        Repository.CompleteSnapshot(currentSnapshot, day3, SnapshotStats.Empty);

        // Retain only the newest day - both expired.txt-related snapshots are eligible.
        var policy = new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0);
        var service = new PruneService(Repository, ContentStore, new FakeRunLock());

        var result = service.Prune(ProfileWithRetention(_targetRoot, policy));

        Assert.Equal(2, result.SnapshotsRemoved);
        Assert.Equal(1, result.BlobsRemoved);
        Assert.DoesNotContain(addedSnapshot, Repository.ListSnapshots().Select(s => s.Id));
        Assert.DoesNotContain(deletedSnapshot, Repository.ListSnapshots().Select(s => s.Id));
        Assert.Contains(currentSnapshot, Repository.ListSnapshots().Select(s => s.Id));
        Assert.False(ContentStore.HasContent(goneHash));
        Assert.True(ContentStore.HasContent(keepHash));
    }

    [Fact]
    public void Content_still_referenced_by_the_current_snapshot_survives_pruning_of_a_superseded_version()
    {
        var (oldHash, _) = ContentStore.StoreFromStream(new MemoryStream("old version"u8.ToArray()));
        var (currentHash, _) = ContentStore.StoreFromStream(new MemoryStream("current version"u8.ToArray()));

        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);

        var oldSnapshot = Repository.BeginSnapshot(day1);
        Repository.RecordFileVersion(oldSnapshot, "current.txt", null, oldHash, 10, day1, FileChangeKind.Added, day1);
        Repository.CompleteSnapshot(oldSnapshot, day1, SnapshotStats.Empty);

        var newSnapshot = Repository.BeginSnapshot(day2);
        Repository.RecordFileVersion(newSnapshot, "current.txt", null, currentHash, 10, day2, FileChangeKind.Changed, day2);
        Repository.CompleteSnapshot(newSnapshot, day2, SnapshotStats.Empty);

        // Retain only the newest daily snapshot - the old (superseded) history is eligible.
        var policy = new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0);
        var service = new PruneService(Repository, ContentStore, new FakeRunLock());

        var result = service.Prune(ProfileWithRetention(_targetRoot, policy));

        Assert.Equal(1, result.SnapshotsRemoved); // the old, superseded snapshot
        Assert.True(ContentStore.HasContent(currentHash)); // still-current content is never GC'd
        Assert.False(ContentStore.HasContent(oldHash)); // superseded, now-unreferenced content is collected
    }

    [Fact]
    public void Prune_never_removes_the_most_recent_completed_snapshot_even_with_an_all_zero_tier_policy()
    {
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var day3 = day1.AddDays(2);

        var addedSnapshot = Repository.BeginSnapshot(day1);
        Repository.RecordFileVersion(addedSnapshot, "a.txt", null, "hash-1", 10, day1, FileChangeKind.Added, day1);
        Repository.CompleteSnapshot(addedSnapshot, day1, SnapshotStats.Empty);

        var changedSnapshot = Repository.BeginSnapshot(day2);
        Repository.RecordFileVersion(changedSnapshot, "a.txt", null, "hash-2", 10, day2, FileChangeKind.Changed, day2);
        Repository.CompleteSnapshot(changedSnapshot, day2, SnapshotStats.Empty);

        // The most recent completed snapshot is a no-op re-run: nothing changed since
        // changedSnapshot, so it records zero file_versions rows and has nothing of its
        // own to anchor it in the manifest store.
        var noOpSnapshot = Repository.BeginSnapshot(day3);
        Repository.CompleteSnapshot(noOpSnapshot, day3, SnapshotStats.Empty);

        // An all-zero-tier policy retains nothing via bucket math, so without the
        // newest-snapshot guarantee every completed snapshot - including noOpSnapshot -
        // would be eligible for removal.
        var policy = new RetentionPolicy(0, 0, 0, 0);
        var service = new PruneService(Repository, ContentStore, new FakeRunLock());

        var result = service.Prune(ProfileWithRetention(_targetRoot, policy));

        Assert.Equal(1, result.SnapshotsRemoved); // only addedSnapshot, superseded by changedSnapshot
        var remainingIds = Repository.ListSnapshots().Select(s => s.Id).ToHashSet();
        Assert.DoesNotContain(addedSnapshot, remainingIds);
        Assert.Contains(changedSnapshot, remainingIds); // still anchors the current row for a.txt
        Assert.Contains(noOpSnapshot, remainingIds); // retained solely via the newest-snapshot guarantee
    }
}
