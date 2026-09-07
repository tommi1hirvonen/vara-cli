using System.Threading;
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
    public void A_tracked_symlink_or_junction_is_not_reported_as_a_missing_or_corrupt_blob()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("hello"u8.ToArray()));
        contentStore.PlaceAtMirrorPath(hash, "a.txt");

        var repository = new FakeSnapshotRepository();
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, "a.txt", null, hash, 5, now, FileChangeKind.Added, now);
        // A Linked row has no content-store hash - it must not be treated as a
        // referenced blob to verify, or it would always report as missing.
        repository.RecordFileVersion(snapshot, "link", null, null, 0, now, FileChangeKind.Linked, now, linkTarget: @"C:\target");
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);

        var service = new IntegrityCheckService(repository, contentStore, new FakeHasher());

        var result = service.Check(quick: false);

        Assert.Equal(1, result.BlobsChecked);
        Assert.Empty(result.Missing);
        Assert.Empty(result.Corrupt);
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

    [Fact]
    public void A_pre_cancelled_token_stops_before_verifying_any_blob()
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
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = service.Check(quick: false, cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.BlobsChecked);
        Assert.Empty(result.Missing);
        Assert.Empty(result.Corrupt);
    }

    [Fact]
    public void Cancelling_mid_run_still_reports_problems_already_found_before_the_stop()
    {
        var contentStore = new FakeContentStore();
        var repository = new FakeSnapshotRepository();
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);

        // First referenced hash is missing (a real problem, found before cancellation);
        // second is intact but never reached because cancellation stops the loop first.
        repository.RecordFileVersion(snapshot, "a.txt", null, "hash-missing", 5, now, FileChangeKind.Added, now);
        var (hashB, _) = contentStore.StoreFromStream(new MemoryStream("hello"u8.ToArray()));
        repository.RecordFileVersion(snapshot, "b.txt", null, hashB, 5, now.AddSeconds(1), FileChangeKind.Added, now.AddSeconds(1));
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);

        var service = new IntegrityCheckService(repository, contentStore, new FakeHasher());
        using var cts = new CancellationTokenSource();

        // Cancels as soon as the first blob has been checked, so the loop's next
        // IsCancellationRequested check (before starting the second blob) stops it early.
        var progress = new CancelAfterReport<IntegrityCheckProgress>(p => p.BlobsChecked == 1, cts);

        var result = service.Check(quick: false, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.BlobsChecked);
        Assert.Single(result.Missing);
    }

    /// <summary>Test seam: an <see cref="IProgress{T}"/> that synchronously cancels <paramref name="cts"/> the first time <paramref name="shouldCancel"/> matches a reported value - unlike <see cref="Progress{T}"/>, which posts to a captured <see cref="SynchronizationContext"/> and would race the loop under test rather than observably cancelling before its next iteration.</summary>
    private sealed class CancelAfterReport<T>(Func<T, bool> shouldCancel, CancellationTokenSource cts) : IProgress<T>
    {
        public void Report(T value)
        {
            if (shouldCancel(value))
            {
                cts.Cancel();
            }
        }
    }
}
