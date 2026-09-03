using Vara.Application.Retention;
using Vara.Application.Tests.Backup;
using Vara.Core.Backup;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.Retention;

public class PruneServiceTests
{
    private static Profile ProfileWithRetention(RetentionPolicy? retention) =>
        new("files", @"D:\backup", [new Source(@"C:\data")], retention);

    [Fact]
    public void Prune_without_a_configured_retention_policy_makes_no_deletions_and_reports_clearly()
    {
        var repository = new FakeSnapshotRepository();
        var service = new PruneService(repository, new FakeContentStore(), new FakeRunLock());

        var ex = Assert.Throws<RetentionPolicyNotConfiguredException>(() => service.Prune(ProfileWithRetention(null)));
        Assert.Equal("files", ex.ProfileName);
    }

    [Fact]
    public void Prune_removes_eligible_snapshots_and_garbage_collects_unreferenced_content()
    {
        var contentStore = new FakeContentStore();
        var (keepHash, _) = contentStore.StoreFromStream(new MemoryStream("kept content"u8.ToArray()));
        var (goneHash, _) = contentStore.StoreFromStream(new MemoryStream("expired content"u8.ToArray()));
        contentStore.PlaceAtMirrorPath(keepHash, "current.txt");

        var repository = new FakeSnapshotRepository();
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var day3 = day1.AddDays(2);

        // "expired.txt" is added, then deleted - both events are themselves old enough
        // to expire, so every reference to goneHash (including the deletion tombstone)
        // is eventually pruned away, making the content truly unreferenced.
        var addedSnapshot = repository.BeginSnapshot(day1);
        repository.RecordFileVersion(addedSnapshot, "expired.txt", null, goneHash, 10, day1, FileChangeKind.Added, day1);
        repository.CompleteSnapshot(addedSnapshot, day1, SnapshotStats.Empty);

        var deletedSnapshot = repository.BeginSnapshot(day2);
        repository.RecordFileVersion(deletedSnapshot, "expired.txt", null, goneHash, 10, day2, FileChangeKind.Deleted, day2);
        repository.CompleteSnapshot(deletedSnapshot, day2, SnapshotStats.Empty);

        var currentSnapshot = repository.BeginSnapshot(day3);
        repository.RecordFileVersion(currentSnapshot, "current.txt", null, keepHash, 10, day3, FileChangeKind.Added, day3);
        repository.CompleteSnapshot(currentSnapshot, day3, SnapshotStats.Empty);

        // Retain only the newest day - both expired.txt-related snapshots are eligible.
        var policy = new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0);
        var service = new PruneService(repository, contentStore, new FakeRunLock());

        var result = service.Prune(ProfileWithRetention(policy));

        Assert.Equal(2, result.SnapshotsRemoved);
        Assert.Equal(1, result.BlobsRemoved);
        Assert.DoesNotContain(addedSnapshot, repository.ListSnapshots().Select(s => s.Id));
        Assert.DoesNotContain(deletedSnapshot, repository.ListSnapshots().Select(s => s.Id));
        Assert.Contains(currentSnapshot, repository.ListSnapshots().Select(s => s.Id));
        Assert.False(contentStore.HasContent(goneHash));
        Assert.True(contentStore.HasContent(keepHash));
    }

    [Fact]
    public void Prune_never_touches_files_currently_present_in_the_live_mirror()
    {
        var contentStore = new FakeContentStore();
        var (oldHash, _) = contentStore.StoreFromStream(new MemoryStream("old version"u8.ToArray()));
        var (currentHash, _) = contentStore.StoreFromStream(new MemoryStream("current version"u8.ToArray()));
        contentStore.PlaceAtMirrorPath(currentHash, "current.txt");

        var repository = new FakeSnapshotRepository();
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);

        var oldSnapshot = repository.BeginSnapshot(day1);
        repository.RecordFileVersion(oldSnapshot, "current.txt", null, oldHash, 10, day1, FileChangeKind.Added, day1);
        repository.CompleteSnapshot(oldSnapshot, day1, SnapshotStats.Empty);

        var newSnapshot = repository.BeginSnapshot(day2);
        repository.RecordFileVersion(newSnapshot, "current.txt", null, currentHash, 10, day2, FileChangeKind.Changed, day2);
        repository.CompleteSnapshot(newSnapshot, day2, SnapshotStats.Empty);

        // Retain only the newest daily snapshot - the old (superseded) history is eligible.
        var policy = new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0);
        var service = new PruneService(repository, contentStore, new FakeRunLock());

        var result = service.Prune(ProfileWithRetention(policy));

        Assert.Equal(1, result.SnapshotsRemoved); // the old, superseded snapshot
        Assert.True(contentStore.Mirror.ContainsKey("current.txt"));
        Assert.Equal(currentHash, contentStore.Mirror["current.txt"]);
        Assert.True(contentStore.HasContent(currentHash)); // still-current content is never GC'd
    }

    [Fact]
    public void Prune_never_removes_the_most_recent_completed_snapshot_even_with_an_all_zero_tier_policy()
    {
        var repository = new FakeSnapshotRepository();
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var day3 = day1.AddDays(2);

        var addedSnapshot = repository.BeginSnapshot(day1);
        repository.RecordFileVersion(addedSnapshot, "a.txt", null, "hash-1", 10, day1, FileChangeKind.Added, day1);
        repository.CompleteSnapshot(addedSnapshot, day1, SnapshotStats.Empty);

        var changedSnapshot = repository.BeginSnapshot(day2);
        repository.RecordFileVersion(changedSnapshot, "a.txt", null, "hash-2", 10, day2, FileChangeKind.Changed, day2);
        repository.CompleteSnapshot(changedSnapshot, day2, SnapshotStats.Empty);

        // The most recent completed snapshot is a no-op re-run: nothing changed since
        // changedSnapshot, so it records zero file_versions rows and has nothing of its
        // own to anchor it in the manifest store.
        var noOpSnapshot = repository.BeginSnapshot(day3);
        repository.CompleteSnapshot(noOpSnapshot, day3, SnapshotStats.Empty);

        // An all-zero-tier policy retains nothing via bucket math, so without the
        // newest-snapshot guarantee every completed snapshot - including noOpSnapshot -
        // would be eligible for removal.
        var policy = new RetentionPolicy(0, 0, 0, 0);
        var service = new PruneService(repository, new FakeContentStore(), new FakeRunLock());

        var result = service.Prune(ProfileWithRetention(policy));

        Assert.Equal(1, result.SnapshotsRemoved); // only addedSnapshot, superseded by changedSnapshot
        var remainingIds = repository.ListSnapshots().Select(s => s.Id).ToHashSet();
        Assert.DoesNotContain(addedSnapshot, remainingIds);
        Assert.Contains(changedSnapshot, remainingIds); // still anchors the current row for a.txt
        Assert.Contains(noOpSnapshot, remainingIds); // retained solely via the newest-snapshot guarantee
    }

    [Fact]
    public void Prune_reports_progress_once_before_and_once_after_each_blob_deletion()
    {
        var contentStore = new FakeContentStore();
        var (keepHash, _) = contentStore.StoreFromStream(new MemoryStream("kept content"u8.ToArray()));
        var (goneHash1, _) = contentStore.StoreFromStream(new MemoryStream("expired content 1"u8.ToArray()));
        var (goneHash2, _) = contentStore.StoreFromStream(new MemoryStream("expired content 2"u8.ToArray()));
        contentStore.PlaceAtMirrorPath(keepHash, "current.txt");

        var repository = new FakeSnapshotRepository();
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var currentSnapshot = repository.BeginSnapshot(day1);
        repository.RecordFileVersion(currentSnapshot, "current.txt", null, keepHash, 10, day1, FileChangeKind.Added, day1);
        repository.CompleteSnapshot(currentSnapshot, day1, SnapshotStats.Empty);

        var service = new PruneService(repository, contentStore, new FakeRunLock());
        var reports = new List<PruneProgress>();
        var progress = new CallbackProgress<PruneProgress>(reports.Add);

        var result = service.Prune(ProfileWithRetention(new RetentionPolicy(1, 0, 0, 0)), progress);

        Assert.Equal(2, result.BlobsRemoved);
        Assert.Equal(
            [
                new PruneProgress(0, 2),
                new PruneProgress(1, 2),
                new PruneProgress(2, 2),
            ],
            reports);
    }

    [Fact]
    public void Prune_sends_no_progress_report_when_there_are_zero_unreferenced_blobs_to_delete()
    {
        var repository = new FakeSnapshotRepository();
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var onlySnapshot = repository.BeginSnapshot(day1);
        repository.RecordFileVersion(onlySnapshot, "current.txt", null, "hash-1", 10, day1, FileChangeKind.Added, day1);
        repository.CompleteSnapshot(onlySnapshot, day1, SnapshotStats.Empty);

        // The single most recent completed snapshot is always retained and its content
        // is the only content in the store, so nothing is eligible for GC.
        var service = new PruneService(repository, new FakeContentStore(), new FakeRunLock());
        var reports = new List<PruneProgress>();
        var progress = new CallbackProgress<PruneProgress>(reports.Add);

        var result = service.Prune(ProfileWithRetention(new RetentionPolicy(1, 0, 0, 0)), progress);

        Assert.Equal(0, result.BlobsRemoved);
        Assert.Empty(reports);
    }

    [Fact]
    public void Prune_throws_when_another_operation_already_holds_the_lock()
    {
        var service = new PruneService(new FakeSnapshotRepository(), new FakeContentStore(), new FakeRunLock(acquirable: false));

        Assert.Throws<PruneAlreadyRunningException>(() => service.Prune(ProfileWithRetention(new RetentionPolicy(1, 0, 0, 0))));
    }

    [Fact]
    public void CountEligibleForRemoval_without_a_configured_retention_policy_throws()
    {
        var repository = new FakeSnapshotRepository();
        var service = new PruneService(repository, new FakeContentStore(), new FakeRunLock());

        var ex = Assert.Throws<RetentionPolicyNotConfiguredException>(() => service.CountEligibleForRemoval(ProfileWithRetention(null)));
        Assert.Equal("files", ex.ProfileName);
    }

    [Fact]
    public void CountEligibleForRemoval_returns_zero_when_nothing_is_eligible()
    {
        var repository = new FakeSnapshotRepository();
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var onlySnapshot = repository.BeginSnapshot(day1);
        repository.RecordFileVersion(onlySnapshot, "current.txt", null, "hash-1", 10, day1, FileChangeKind.Added, day1);
        repository.CompleteSnapshot(onlySnapshot, day1, SnapshotStats.Empty);

        // The single most recent completed snapshot is always retained, so with only
        // one snapshot recorded, nothing is ever eligible for removal.
        var policy = new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0);
        var service = new PruneService(repository, new FakeContentStore(), new FakeRunLock());

        Assert.Equal(0, service.CountEligibleForRemoval(ProfileWithRetention(policy)));
    }

    [Fact]
    public void CountEligibleForRemoval_returns_the_number_of_snapshots_eligible_for_removal()
    {
        var repository = new FakeSnapshotRepository();
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var day3 = day1.AddDays(2);

        var oldSnapshot = repository.BeginSnapshot(day1);
        repository.RecordFileVersion(oldSnapshot, "current.txt", null, "hash-1", 10, day1, FileChangeKind.Added, day1);
        repository.CompleteSnapshot(oldSnapshot, day1, SnapshotStats.Empty);

        var middleSnapshot = repository.BeginSnapshot(day2);
        repository.RecordFileVersion(middleSnapshot, "current.txt", null, "hash-2", 10, day2, FileChangeKind.Changed, day2);
        repository.CompleteSnapshot(middleSnapshot, day2, SnapshotStats.Empty);

        var newestSnapshot = repository.BeginSnapshot(day3);
        repository.RecordFileVersion(newestSnapshot, "current.txt", null, "hash-3", 10, day3, FileChangeKind.Changed, day3);
        repository.CompleteSnapshot(newestSnapshot, day3, SnapshotStats.Empty);

        // Retain only the newest daily snapshot - both older snapshots are eligible.
        var policy = new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0);
        var service = new PruneService(repository, new FakeContentStore(), new FakeRunLock());

        Assert.Equal(2, service.CountEligibleForRemoval(ProfileWithRetention(policy)));

        // A read-only preview: no snapshot records or stored content are affected by
        // calling it, and a subsequent real Prune still sees (and removes) the same
        // snapshots the preview counted.
        Assert.Equal(3, repository.ListSnapshots().Count);
        var result = service.Prune(ProfileWithRetention(policy));
        Assert.Equal(2, result.SnapshotsRemoved);
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> invoking an arbitrary callback on the
    /// reporting thread directly, mirroring BackupPipelineTests's CallbackProgress - Prune's
    /// deletion loop is single-threaded and strictly sequential, so no synchronization is
    /// needed here.</summary>
    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
