using Vara.Application.Integrity;
using Vara.Application.Tests.Backup;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.Integrity;

public class IntegrityCheckServiceTests
{
    [Fact]
    public void All_intact_content_reports_no_findings()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("hello"u8.ToArray()));
        contentStore.PlaceAtMirrorPath(hash, "a.txt");

        var repository = new FakeSnapshotRepository();
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, "a.txt", null, hash, 5, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);

        var service = new IntegrityCheckService(repository, contentStore, new FakeHasher());

        var result = service.Check(quick: false);

        Assert.Equal(1, result.BlobsChecked);
        Assert.Empty(result.Missing);
        Assert.Empty(result.Corrupt);
        Assert.Empty(result.Orphaned);
    }

    [Fact]
    public void A_referenced_blob_absent_from_the_store_is_reported_as_missing_with_its_affected_path()
    {
        var contentStore = new FakeContentStore();
        var repository = new FakeSnapshotRepository();
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);

        // Referenced by the manifest but never actually stored (simulates a blob lost
        // from the target, e.g. to a disk error).
        repository.RecordFileVersion(snapshot, "a.txt", null, "hash-missing", 5, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);

        var service = new IntegrityCheckService(repository, contentStore, new FakeHasher());

        var result = service.Check(quick: false);

        var finding = Assert.Single(result.Missing);
        Assert.Equal("hash-missing", finding.ContentHash);
        Assert.Contains("a.txt", finding.AffectedPaths);
        Assert.Empty(result.Corrupt);
    }

    [Fact]
    public void A_corrupted_blob_is_reported_as_corrupt_in_full_mode()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("original content"u8.ToArray()));
        contentStore.PlaceAtMirrorPath(hash, "a.txt");
        contentStore.CorruptBlob(hash, "corrupted content");

        var repository = new FakeSnapshotRepository();
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, "a.txt", null, hash, 16, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);

        var service = new IntegrityCheckService(repository, contentStore, new FakeHasher());

        var result = service.Check(quick: false);

        var finding = Assert.Single(result.Corrupt);
        Assert.Equal(hash, finding.ContentHash);
        Assert.Contains("a.txt", finding.AffectedPaths);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void Quick_mode_does_not_detect_a_corrupted_but_present_blob()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("original content"u8.ToArray()));
        contentStore.PlaceAtMirrorPath(hash, "a.txt");
        contentStore.CorruptBlob(hash, "corrupted content");

        var repository = new FakeSnapshotRepository();
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, "a.txt", null, hash, 16, now, FileChangeKind.Added, now);

        // Also reference a hash never stored at all, alongside the corrupted one, to
        // prove quick mode still reports a missing blob "exactly as the default mode
        // would" per the backup-integrity spec's quick-mode scenario - it is only
        // content re-hashing (corruption detection) that quick mode skips.
        repository.RecordFileVersion(snapshot, "b.txt", null, "hash-missing", 5, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);

        var service = new IntegrityCheckService(repository, contentStore, new FakeHasher());

        var result = service.Check(quick: true);

        // Present (even though corrupted), so quick mode - which never re-hashes -
        // reports it as neither missing nor corrupt.
        Assert.Empty(result.Corrupt);

        // Absent, so still reported as missing even in quick mode.
        var finding = Assert.Single(result.Missing);
        Assert.Equal("hash-missing", finding.ContentHash);
    }

    [Fact]
    public void An_orphaned_blob_is_reported_without_being_deleted()
    {
        var contentStore = new FakeContentStore();
        var (orphanHash, _) = contentStore.StoreFromStream(new MemoryStream("unreferenced"u8.ToArray()));

        // No manifest reference to orphanHash at all: it's physically present but unreferenced.
        var repository = new FakeSnapshotRepository();

        var service = new IntegrityCheckService(repository, contentStore, new FakeHasher());

        var result = service.Check(quick: false);

        Assert.Contains(orphanHash, result.Orphaned);
        Assert.Empty(result.Missing);
        Assert.Empty(result.Corrupt);
        Assert.True(contentStore.HasContent(orphanHash)); // never deleted by the check
    }
}
