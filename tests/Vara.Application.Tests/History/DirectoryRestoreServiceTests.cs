using Vara.Application.History;
using Vara.Application.Tests.Backup;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.History;

public class DirectoryRestoreServiceTests
{
    [Fact]
    public void PlanDirectoryRestore_includes_files_live_at_the_requested_date_and_excludes_files_added_later()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\b.txt", null, "hash-2", 5, t1, FileChangeKind.Added, t1);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(12), outRoot: @"C:\out", inPlace: false);

        var entry = Assert.Single(plan.ToWrite);
        Assert.Equal(@"src\a.txt", entry.RelativePath);
        Assert.Equal(@"C:\out\a.txt", entry.DestinationPath);
        Assert.Equal(10, plan.TotalBytes);
    }

    [Fact]
    public void PlanDirectoryRestore_includes_a_file_deleted_after_the_requested_date()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(12), outRoot: @"C:\out", inPlace: false);

        Assert.Single(plan.ToWrite, e => e.RelativePath == @"src\a.txt");
    }

    [Fact]
    public void PlanDirectoryRestore_excludes_a_tracked_symlink_from_ToWrite_and_reports_it_as_skipped()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"src\link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var plan = service.PlanDirectoryRestore("src", asOf: null, outRoot: @"C:\out", inPlace: false);

        Assert.Single(plan.ToWrite, e => e.RelativePath == @"src\a.txt");
        Assert.DoesNotContain(plan.ToWrite, e => e.RelativePath == @"src\link");
        Assert.Contains(@"src\link", plan.Skipped);
        Assert.DoesNotContain(@"src\link", plan.ToRemove);
    }

    [Fact]
    public void PlanDirectoryRestore_does_not_remove_a_symlink_that_is_live_at_the_requested_date_even_if_a_stale_destination_file_exists()
    {
        // A symlink/junction live at the requested date contributes nothing to ToWrite
        // (it has no stored content), but it must still count as "live at that date" for
        // ToRemove purposes - otherwise it would look indistinguishable from a path that
        // was never live at all, and get spuriously removed.
        var contentStore = new FakeContentStore();
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        contentStore.SeedExistingTarget(@"C:\out\link");
        var service = new DirectoryRestoreService(repository, contentStore);

        var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(1), outRoot: @"C:\out", inPlace: false);

        Assert.DoesNotContain(@"C:\out\link", plan.ToRemove);
        Assert.Contains(@"src\link", plan.Skipped);
    }

    [Fact]
    public void PlanDirectoryRestore_computes_removals_for_paths_live_now_but_not_at_the_requested_date()
    {
        var contentStore = new FakeContentStore();
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\b.txt", null, "hash-2", 5, t1, FileChangeKind.Added, t1);
        contentStore.SeedExistingTarget(@"C:\out\b.txt");
        var service = new DirectoryRestoreService(repository, contentStore);

        var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(12), outRoot: @"C:\out", inPlace: false);

        Assert.Single(plan.ToWrite, e => e.RelativePath == @"src\a.txt");
        Assert.Single(plan.ToRemove, d => d == @"C:\out\b.txt");
    }

    [Fact]
    public void PlanDirectoryRestore_does_not_remove_a_path_not_currently_present_at_its_destination()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\b.txt", null, "hash-2", 5, t1, FileChangeKind.Added, t1);
        // Note: no SeedExistingTarget for b.txt's destination - nothing physically there to remove.
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(12), outRoot: @"C:\out", inPlace: false);

        Assert.Empty(plan.ToRemove);
    }

    [Fact]
    public void PlanDirectoryRestore_in_place_maps_each_entry_to_its_own_original_source_location()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"C\Users\john\Documents\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var plan = service.PlanDirectoryRestore(@"C\Users\john\Documents", asOf: null, outRoot: null, inPlace: true);

        var entry = Assert.Single(plan.ToWrite);
        Assert.Equal(@"C:\Users\john\Documents\a.txt", entry.DestinationPath);
    }

    [Fact]
    public void PlanDirectoryRestore_throws_RestoreDestinationInMirror_for_a_write_destination_inside_the_mirror()
    {
        var contentStore = new FakeContentStore();
        contentStore.MirrorPaths.Add(@"C:\out\a.txt");
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var service = new DirectoryRestoreService(repository, contentStore);

        var ex = Assert.Throws<RestoreDestinationInMirrorException>(
            () => service.PlanDirectoryRestore("src", asOf: null, outRoot: @"C:\out", inPlace: false));
        Assert.Equal(@"C:\out\a.txt", ex.DestinationPath);
    }

    [Fact]
    public void PlanDirectoryRestore_for_a_never_tracked_directory_throws_NoSuchDirectory()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(
            s1, @"src\a.txt", null, "hash-1", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var ex = Assert.Throws<NoSuchDirectoryException>(
            () => service.PlanDirectoryRestore("never-tracked-dir", asOf: null, outRoot: @"C:\out", inPlace: false));
        Assert.Equal("never-tracked-dir", ex.DirectoryPath);
    }

    [Fact]
    public void ExecuteDirectoryRestore_writes_planned_entries_and_removes_planned_destinations()
    {
        var contentStore = new FakeContentStore();
        var (hashA, sizeA) = contentStore.StoreFromStream(new MemoryStream("content a"u8.ToArray()));
        var (hashC, sizeC) = contentStore.StoreFromStream(new MemoryStream("content c"u8.ToArray()));
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vara-dirrestore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var destinationA = Path.Combine(tempRoot, "a.txt");
        var destinationC = Path.Combine(tempRoot, "sub", "c.txt");
        var toRemove = Path.Combine(tempRoot, "stale.txt");
        File.WriteAllText(toRemove, "should be removed");

        try
        {
            var plan = new DirectoryRestorePlan(
                ToWrite:
                [
                    new DirectoryRestoreEntry(@"src\a.txt", hashA, sizeA, destinationA),
                    new DirectoryRestoreEntry(@"src\sub\c.txt", hashC, sizeC, destinationC),
                ],
                ToRemove: [toRemove],
                TotalBytes: sizeA + sizeC,
                Skipped: []);
            var service = new DirectoryRestoreService(new FakeSnapshotRepository(), contentStore);

            var chunkReports = new List<long>();
            long? resolvedSize = null;

            service.ExecuteDirectoryRestore(
                plan,
                onBytesCopied: bytes => chunkReports.Add(bytes),
                onSizeResolved: resolved => resolvedSize = resolved);

            Assert.Equal("content a", File.ReadAllText(destinationA));
            Assert.Equal("content c", File.ReadAllText(destinationC));
            Assert.False(File.Exists(toRemove));
            Assert.Equal(plan.TotalBytes, resolvedSize);
            Assert.Equal(plan.TotalBytes, chunkReports.Sum());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ExecuteDirectoryRestore_performs_no_io_for_a_skipped_entry_and_does_not_throw()
    {
        var contentStore = new FakeContentStore();
        var (hashA, sizeA) = contentStore.StoreFromStream(new MemoryStream("content a"u8.ToArray()));
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vara-dirrestore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var destinationA = Path.Combine(tempRoot, "a.txt");
        var linkDestination = Path.Combine(tempRoot, "link");

        try
        {
            var plan = new DirectoryRestorePlan(
                ToWrite: [new DirectoryRestoreEntry(@"src\a.txt", hashA, sizeA, destinationA)],
                ToRemove: [],
                TotalBytes: sizeA,
                Skipped: [@"src\link"]);
            var service = new DirectoryRestoreService(new FakeSnapshotRepository(), contentStore);

            service.ExecuteDirectoryRestore(plan);

            Assert.Equal("content a", File.ReadAllText(destinationA));
            Assert.False(File.Exists(linkDestination));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ExecuteDirectoryRestore_with_a_pre_cancelled_token_writes_and_removes_nothing_and_returns_true()
    {
        var contentStore = new FakeContentStore();
        var (hashA, sizeA) = contentStore.StoreFromStream(new MemoryStream("content a"u8.ToArray()));
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vara-dirrestore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var destinationA = Path.Combine(tempRoot, "a.txt");
        var toRemove = Path.Combine(tempRoot, "stale.txt");
        File.WriteAllText(toRemove, "should stay - cancelled before this removal ran");

        try
        {
            var plan = new DirectoryRestorePlan(
                ToWrite: [new DirectoryRestoreEntry(@"src\a.txt", hashA, sizeA, destinationA)],
                ToRemove: [toRemove],
                TotalBytes: sizeA,
                Skipped: []);
            var service = new DirectoryRestoreService(new FakeSnapshotRepository(), contentStore);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var stoppedEarly = service.ExecuteDirectoryRestore(plan, cancellationToken: cts.Token);

            Assert.True(stoppedEarly);
            Assert.False(File.Exists(destinationA));
            Assert.True(File.Exists(toRemove));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ExecuteDirectoryRestore_cancelled_between_writes_keeps_already_written_files_and_stops_before_the_rest()
    {
        var contentStore = new FakeContentStore();
        var (hashA, sizeA) = contentStore.StoreFromStream(new MemoryStream("content a"u8.ToArray()));
        var (hashB, sizeB) = contentStore.StoreFromStream(new MemoryStream("content b"u8.ToArray()));
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vara-dirrestore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var destinationA = Path.Combine(tempRoot, "a.txt");
        var destinationB = Path.Combine(tempRoot, "b.txt");

        try
        {
            var plan = new DirectoryRestorePlan(
                ToWrite:
                [
                    new DirectoryRestoreEntry(@"src\a.txt", hashA, sizeA, destinationA),
                    new DirectoryRestoreEntry(@"src\b.txt", hashB, sizeB, destinationB),
                ],
                ToRemove: [],
                TotalBytes: sizeA + sizeB,
                Skipped: []);
            var service = new DirectoryRestoreService(new FakeSnapshotRepository(), contentStore);
            using var cts = new CancellationTokenSource();

            // Cancels once the first file's bytes have been fully copied, so the loop's next
            // IsCancellationRequested check (before starting the second file's write) stops it
            // early - mirroring a real Ctrl+C landing between two file writes.
            var bytesCopiedForA = 0L;
            void OnBytesCopied(long bytes)
            {
                bytesCopiedForA += bytes;
                if (bytesCopiedForA >= sizeA)
                {
                    cts.Cancel();
                }
            }

            var stoppedEarly = service.ExecuteDirectoryRestore(plan, OnBytesCopied, cancellationToken: cts.Token);

            Assert.True(stoppedEarly);
            Assert.Equal("content a", File.ReadAllText(destinationA)); // already-applied write stays applied
            Assert.False(File.Exists(destinationB)); // never started
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ListDirectorySnapshotCandidates_for_a_directory_with_no_history_returns_an_empty_list_without_throwing()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(
            s1, @"other\a.txt", null, "hash-1", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        repository.CompleteSnapshot(s1, DateTimeOffset.UtcNow, SnapshotStats.Empty);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var candidates = service.ListDirectorySnapshotCandidates("src");

        Assert.Empty(candidates);
    }

    [Fact]
    public void ListDirectorySnapshotCandidates_returns_multiple_qualifying_snapshots_most_recent_first()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\a.txt", null, "hash-2", 20, t1, FileChangeKind.Changed, t1);
        repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var candidates = service.ListDirectorySnapshotCandidates("src");

        Assert.Equal([s2, s1], candidates.Select(c => c.Snapshot.Id));
    }

    [Fact]
    public void ListDirectorySnapshotCandidates_excludes_a_Running_or_Failed_snapshot_that_touched_the_directory()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var runningSnapshot = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(runningSnapshot, @"src\b.txt", null, "hash-2", 5, t1, FileChangeKind.Added, t1);
        // Left in Running status - never completed/failed/cancelled.

        var t2 = t1.AddDays(1);
        var failedSnapshot = repository.BeginSnapshot(t2);
        repository.RecordFileVersion(failedSnapshot, @"src\c.txt", null, "hash-3", 5, t2, FileChangeKind.Added, t2);
        repository.FailSnapshot(failedSnapshot, t2, SnapshotStats.Empty);

        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var candidates = service.ListDirectorySnapshotCandidates("src");

        Assert.Equal([s1], candidates.Select(c => c.Snapshot.Id));
    }

    [Fact]
    public void ListDirectorySnapshotCandidates_excludes_a_snapshot_that_only_touched_an_unrelated_path()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"other\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var candidates = service.ListDirectorySnapshotCandidates("src");

        Assert.Empty(candidates);
    }

    [Fact]
    public void ListDirectorySnapshotCandidates_reports_directory_scoped_delta_counts_and_net_bytes()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\added.txt", null, "hash-added", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"src\changed.txt", null, "hash-changed-1", 5, t0, FileChangeKind.Added, t0);
        repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\changed.txt", null, "hash-changed-2", 8, t1, FileChangeKind.Changed, t1);
        repository.RecordFileVersion(s2, @"src\deleted.txt", null, null, 0, t1, FileChangeKind.Deleted, t1);
        repository.RecordFileVersion(s2, @"src\old.txt", null, "hash-old", 4, t1, FileChangeKind.Deleted, t1);
        repository.RecordFileVersion(s2, @"src\moved.txt", @"other\moved.txt", "hash-moved", 6, t1, FileChangeKind.Moved, t1);
        repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var candidates = service.ListDirectorySnapshotCandidates("src");
        var latest = candidates.Single(c => c.Snapshot.Id == s2);

        Assert.Equal(0, latest.FilesAdded);
        Assert.Equal(1, latest.FilesChanged);
        Assert.Equal(1, latest.FilesMoved);
        Assert.Equal(2, latest.FilesDeleted);
        // +8 (changed) - 0 (deleted, size 0) - 4 (deleted, size 4) + 6 (moved in) = 10
        Assert.Equal(10, latest.NetBytesDelta);
    }

    [Fact]
    public void ListDirectorySnapshotCandidates_reports_point_in_time_totals_with_symlinks_counted_separately()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-a", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"src\b.txt", null, "hash-b", 15, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"src\link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var candidate = Assert.Single(service.ListDirectorySnapshotCandidates("src"));

        Assert.Equal(2, candidate.TotalFiles);
        Assert.Equal(25, candidate.TotalBytes);
        Assert.Equal(1, candidate.TotalLinks);
    }

    [Fact]
    public void ResolveDirectorySnapshot_for_a_valid_id_returns_its_StartedAt()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var resolved = service.ResolveDirectorySnapshot("src", s1);

        Assert.Equal(t0, resolved);
    }

    [Fact]
    public void ResolveDirectorySnapshot_for_an_id_from_an_unrelated_directory_throws()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"other\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        var ex = Assert.Throws<NoMatchingDirectorySnapshotException>(() => service.ResolveDirectorySnapshot("src", s1));
        Assert.Equal("src", ex.DirectoryPath);
        Assert.Equal(s1, ex.SnapshotId);
    }

    [Fact]
    public void ResolveDirectorySnapshot_for_a_Running_or_Failed_snapshot_throws()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var running = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(running, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var service = new DirectoryRestoreService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingDirectorySnapshotException>(() => service.ResolveDirectorySnapshot("src", running));

        var failed = repository.BeginSnapshot(t0.AddDays(1));
        repository.RecordFileVersion(failed, @"src\b.txt", null, "hash-2", 10, t0.AddDays(1), FileChangeKind.Added, t0.AddDays(1));
        repository.FailSnapshot(failed, t0.AddDays(1), SnapshotStats.Empty);

        Assert.Throws<NoMatchingDirectorySnapshotException>(() => service.ResolveDirectorySnapshot("src", failed));
    }
}
