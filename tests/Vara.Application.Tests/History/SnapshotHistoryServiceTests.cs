using Vara.Application.History;
using Vara.Application.Tests.Backup;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.History;

public class SnapshotHistoryServiceTests
{
    [Fact]
    public void ListSnapshots_returns_recorded_snapshots_most_recent_first()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        repository.CompleteSnapshot(s1, DateTimeOffset.UtcNow, SnapshotStats.Empty);
        var s2 = repository.BeginSnapshot(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        repository.CompleteSnapshot(s2, DateTimeOffset.UtcNow, SnapshotStats.Empty);

        var service = new SnapshotHistoryService(repository);

        var snapshots = service.ListSnapshots();

        Assert.Equal([s2, s1], snapshots.Select(s => s.Id));
    }

    [Fact]
    public void GetSnapshotChanges_rolls_up_all_change_kinds_to_scope_and_ancestor_directories()
    {
        var repository = new FakeSnapshotRepository();
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, @"src\added.txt", null, "hash-a", 10, now, FileChangeKind.Added, now);
        repository.RecordFileVersion(snapshot, @"src\nested\changed.txt", null, "hash-c", 20, now, FileChangeKind.Changed, now);
        repository.RecordFileVersion(snapshot, @"src\nested\moved.txt", @"old\moved.txt", "hash-m", 30, now, FileChangeKind.Moved, now);
        repository.RecordFileVersion(snapshot, @"src\deleted.txt", null, null, 0, now, FileChangeKind.Deleted, now);
        repository.RecordFileVersion(snapshot, @"src\link", null, null, 0, now, FileChangeKind.Linked, now, linkTarget: @"C:\target");
        repository.RecordFileVersion(snapshot, @"other\ignored.txt", null, "hash-o", 1, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);
        var service = new SnapshotHistoryService(repository);

        var report = service.GetSnapshotChanges(snapshot, ".", includeFiles: true)!;
        var root = Assert.Single(report.Directories, d => d.RelativePath.Length == 0);
        var src = Assert.Single(report.Directories, d => d.RelativePath == "src");
        var nested = Assert.Single(report.Directories, d => d.RelativePath == @"src\nested");

        Assert.Equal((2, 1, 1, 1, 1), (root.FilesAdded, root.FilesChanged, root.FilesMoved, root.FilesDeleted, root.FilesLinked));
        Assert.Equal((1, 1, 1, 1, 1), (src.FilesAdded, src.FilesChanged, src.FilesMoved, src.FilesDeleted, src.FilesLinked));
        Assert.Equal((0, 1, 1, 0, 0), (nested.FilesAdded, nested.FilesChanged, nested.FilesMoved, nested.FilesDeleted, nested.FilesLinked));
        Assert.Equal(@"old\moved.txt", Assert.Single(nested.Files, f => f.ChangeKind == FileChangeKind.Moved).PreviousRelativePath);
    }

    [Fact]
    public void GetSnapshotChanges_scopes_rows_and_omits_file_details_by_default()
    {
        var repository = new FakeSnapshotRepository();
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, @"src\one\a.txt", null, "hash-a", 10, now, FileChangeKind.Added, now);
        repository.RecordFileVersion(snapshot, @"src\two\b.txt", null, "hash-b", 10, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);
        var service = new SnapshotHistoryService(repository);

        var report = service.GetSnapshotChanges(snapshot, @"src\one")!;

        var scope = Assert.Single(report.Directories);
        Assert.Equal(@"src\one", scope.RelativePath);
        Assert.Equal(1, scope.FilesAdded);
        Assert.Empty(scope.Files);
    }

    [Fact]
    public void GetSnapshotChanges_reports_no_events_for_an_empty_scope_and_null_for_unknown_snapshot()
    {
        var repository = new FakeSnapshotRepository();
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, @"src\a.txt", null, "hash-a", 10, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);
        var service = new SnapshotHistoryService(repository);

        Assert.Empty(service.GetSnapshotChanges(snapshot, "missing")!.Directories);
        Assert.Null(service.GetSnapshotChanges(snapshot + 1, "."));
    }

    [Fact]
    public void GetFileHistory_for_a_tracked_path_returns_its_versions()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash-1", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new SnapshotHistoryService(repository);

        var history = service.GetFileHistory("a.txt");

        Assert.Single(history);
    }

    [Fact]
    public void GetFileHistory_for_an_untracked_path_throws_a_clear_error()
    {
        var service = new SnapshotHistoryService(new FakeSnapshotRepository());

        var ex = Assert.Throws<NoHistoryForPathException>(() => service.GetFileHistory("never-tracked.txt"));
        Assert.Equal("never-tracked.txt", ex.RelativePath);
    }

    [Fact]
    public void ListDirectory_returns_only_live_entries_by_default()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\main.py", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"src\utils\helper.py", null, "hash-2", 20, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"notes.txt", null, "hash-3", 5, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(repository);

        var entries = service.ListDirectory("src", asOf: null, includeDeleted: false);

        Assert.Equal(2, entries.Count);
        var file = Assert.Single(entries, e => e.Name == "main.py");
        Assert.Equal(DirectoryEntryKind.File, file.Kind);
        Assert.Equal(DirectoryEntryStatus.Live, file.Status);
        var dir = Assert.Single(entries, e => e.Name == "utils");
        Assert.Equal(DirectoryEntryKind.Directory, dir.Kind);
        Assert.Equal(DirectoryEntryStatus.Live, dir.Status);
    }

    [Fact]
    public void ListDirectory_as_of_a_past_date_excludes_entries_added_later()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\main.py", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\new_file.py", null, "hash-2", 5, t1, FileChangeKind.Added, t1);
        var service = new SnapshotHistoryService(repository);

        var entries = service.ListDirectory("src", asOf: t0.AddHours(12), includeDeleted: false);

        Assert.Single(entries, e => e.Name == "main.py");
        Assert.DoesNotContain(entries, e => e.Name == "new_file.py");
    }

    [Fact]
    public void ListDirectory_with_includeDeleted_interleaves_a_deleted_entry_marked_distinctly()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\main.py", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"src\old.py", null, "hash-2", 5, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\old.py", null, "hash-2", 5, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository);

        var withoutDeleted = service.ListDirectory("src", asOf: null, includeDeleted: false);
        Assert.Single(withoutDeleted);

        var withDeleted = service.ListDirectory("src", asOf: null, includeDeleted: true);
        Assert.Equal(2, withDeleted.Count);
        var deletedEntry = Assert.Single(withDeleted, e => e.Name == "old.py");
        Assert.Equal(DirectoryEntryStatus.Deleted, deletedEntry.Status);
    }

    [Fact]
    public void ListDirectory_marks_an_entry_moved_out_of_the_directory_as_moved_not_deleted()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        repository.RecordFileVersion(s2, @"archive\a.txt", @"src\a.txt", "hash-1", 10, t1, FileChangeKind.Moved, t1);
        var service = new SnapshotHistoryService(repository);

        var entries = service.ListDirectory("src", asOf: null, includeDeleted: true);

        var movedEntry = Assert.Single(entries, e => e.Name == "a.txt");
        Assert.Equal(DirectoryEntryStatus.Moved, movedEntry.Status);
        Assert.Equal(@"archive\a.txt", movedEntry.MovedTo);
    }

    [Fact]
    public void ListDirectory_still_lists_a_wholly_deleted_subdirectory_when_including_deleted_entries()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\gone\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\gone\a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository);

        Assert.Empty(service.ListDirectory("src", asOf: null, includeDeleted: false));

        var entries = service.ListDirectory("src", asOf: null, includeDeleted: true);
        var dir = Assert.Single(entries, e => e.Name == "gone");
        Assert.Equal(DirectoryEntryKind.Directory, dir.Kind);
        Assert.Equal(DirectoryEntryStatus.Deleted, dir.Status);
    }

    [Fact]
    public void ListDirectory_ignores_tombstones_outside_the_directory_and_prefers_a_live_entry_over_its_own_tombstone()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\readded.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"other\gone.txt", null, "hash-2", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\readded.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        repository.RecordFileVersion(s2, @"other\gone.txt", null, "hash-2", 10, t1, FileChangeKind.Deleted, t1);
        var t2 = t1.AddDays(1);
        var s3 = repository.BeginSnapshot(t2);
        repository.RecordFileVersion(s3, @"src\readded.txt", null, "hash-3", 12, t2, FileChangeKind.Added, t2);
        var service = new SnapshotHistoryService(repository);

        var entries = service.ListDirectory("src", asOf: null, includeDeleted: true);

        var entry = Assert.Single(entries);
        Assert.Equal("readded.txt", entry.Name);
        Assert.Equal(DirectoryEntryStatus.Live, entry.Status);
    }

    [Fact]
    public void ListDirectory_for_a_never_tracked_directory_throws_NoSuchDirectory()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(
            s1, @"src\a.txt", null, "hash-1", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new SnapshotHistoryService(repository);

        var ex = Assert.Throws<NoSuchDirectoryException>(
            () => service.ListDirectory("never-tracked-dir", asOf: null, includeDeleted: false));
        Assert.Equal("never-tracked-dir", ex.DirectoryPath);
    }

    [Fact]
    public void ListDeleted_lists_every_currently_deleted_path_across_the_profile()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"docs\b.txt", null, "hash-2", 5, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        repository.RecordFileVersion(s2, @"docs\b.txt", null, "hash-2", 5, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository);

        var deleted = service.ListDeleted(directoryPath: null, since: null);

        Assert.Equal(2, deleted.Count);
    }

    [Fact]
    public void ListDeleted_scoped_to_a_subtree_excludes_deletions_outside_it()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"docs\b.txt", null, "hash-2", 5, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        repository.RecordFileVersion(s2, @"docs\b.txt", null, "hash-2", 5, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository);

        var deleted = service.ListDeleted(directoryPath: "src", since: null);

        var entry = Assert.Single(deleted);
        Assert.Equal(@"src\a.txt", entry.RelativePath);
    }

    [Fact]
    public void ListDeleted_with_no_deleted_files_returns_an_empty_list()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash-1", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new SnapshotHistoryService(repository);

        Assert.Empty(service.ListDeleted(directoryPath: null, since: null));
    }
}
