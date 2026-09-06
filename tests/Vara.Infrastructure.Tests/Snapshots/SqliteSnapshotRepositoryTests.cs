using Microsoft.Data.Sqlite;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Snapshots;
using Xunit;

namespace Vara.Infrastructure.Tests.Snapshots;

public class SqliteSnapshotRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vara-test-{Guid.NewGuid():N}.db");
    private SqliteSnapshotRepository? _repository;

    private SqliteSnapshotRepository Repository => _repository ??= new SqliteSnapshotRepository(_dbPath);

    public void Dispose()
    {
        _repository?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void A_fresh_database_initializes_the_expected_schema()
    {
        _ = Repository; // trigger creation

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using var reader = command.ExecuteReader();

        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("snapshots", tables);
        Assert.Contains("file_versions", tables);
    }

    [Fact]
    public void A_fresh_database_declares_relative_path_with_nocase_collation()
    {
        _ = Repository; // trigger creation

        Assert.Contains("COLLATE NOCASE", GetFileVersionsTableSql(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_database_created_before_the_collation_fix_is_migrated_and_its_rows_survive_intact()
    {
        // Seed a fixture shaped like a pre-fix profile.db: file_versions without
        // COLLATE NOCASE on relative_path, populated directly (bypassing the repository,
        // which always creates the corrected schema).
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    status TEXT NOT NULL,
                    bytes_transferred INTEGER NOT NULL DEFAULT 0,
                    files_added INTEGER NOT NULL DEFAULT 0,
                    files_changed INTEGER NOT NULL DEFAULT 0,
                    files_moved INTEGER NOT NULL DEFAULT 0,
                    files_deleted INTEGER NOT NULL DEFAULT 0,
                    files_failed INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE file_versions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
                    relative_path TEXT NOT NULL,
                    previous_relative_path TEXT NULL,
                    content_hash TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    source_modified_at TEXT NOT NULL,
                    change_kind TEXT NOT NULL,
                    recorded_at TEXT NOT NULL,
                    quick_hash TEXT NULL,
                    quick_hash_scheme INTEGER NULL
                );

                INSERT INTO snapshots (id, started_at, completed_at, status)
                VALUES (1, '2026-01-01T00:00:00Z', '2026-01-01T00:05:00Z', 'Complete');

                INSERT INTO file_versions
                    (id, snapshot_id, relative_path, previous_relative_path, content_hash, size, source_modified_at, change_kind, recorded_at, quick_hash, quick_hash_scheme)
                VALUES
                    (1, 1, 'a.txt', NULL, 'hash-a', 10, '2026-01-01T00:00:00Z', 'Added', '2026-01-01T00:00:00Z', NULL, NULL),
                    (2, 1, 'B.txt', NULL, 'hash-b', 20, '2026-01-01T00:00:00Z', 'Added', '2026-01-01T00:00:00Z', 'quick-b', 1);
                """;
            create.ExecuteNonQuery();
        }

        // Opening the repository against the pre-existing file triggers the migration.
        _ = Repository;

        Assert.Contains("COLLATE NOCASE", GetFileVersionsTableSql(), StringComparison.OrdinalIgnoreCase);

        var current = Repository.GetCurrentState();
        Assert.Equal(2, current.Count);
        Assert.Equal("hash-a", current["a.txt"].ContentHash);
        Assert.Equal(10, current["a.txt"].Size);
        Assert.Equal("hash-b", current["B.txt"].ContentHash);
        Assert.Equal(20, current["B.txt"].Size);
        Assert.Equal("quick-b", current["B.txt"].QuickHash);
        Assert.Equal(1, current["B.txt"].QuickHashScheme);

        // Future writes must still auto-increment past the migrated rows' ids.
        var snapshotId = Repository.BeginSnapshot(DateTimeOffset.UtcNow);
        Repository.RecordFileVersion(snapshotId, "c.txt", null, "hash-c", 30, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var history = Repository.GetFileHistory("c.txt");
        Assert.True(Assert.Single(history).Id > 2);
    }

    private string GetFileVersionsTableSql()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'file_versions'";
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public void Begin_and_complete_snapshot_round_trips_status_and_stats()
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddMinutes(5);
        var stats = new SnapshotStats(BytesTransferred: 1024, FilesAdded: 2, FilesChanged: 1, FilesMoved: 0, FilesDeleted: 0, FilesFailed: 0);

        var id = Repository.BeginSnapshot(startedAt);
        Repository.CompleteSnapshot(id, completedAt, stats);

        var snapshot = Assert.Single(Repository.ListSnapshots());
        Assert.Equal(SnapshotStatus.Complete, snapshot.Status);
        Assert.Equal(completedAt, snapshot.CompletedAt);
        Assert.Equal(stats, snapshot.Stats);
    }

    [Fact]
    public void ReconcileIncompleteSnapshots_marks_running_snapshots_as_failed()
    {
        var id = Repository.BeginSnapshot(DateTimeOffset.UtcNow);

        Repository.ReconcileIncompleteSnapshots();

        var snapshot = Repository.ListSnapshots().Single(s => s.Id == id);
        Assert.Equal(SnapshotStatus.Failed, snapshot.Status);
    }

    [Fact]
    public void GetCurrentState_reflects_the_latest_non_deleted_row_per_path()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot1 = Repository.BeginSnapshot(now);
        Repository.RecordFileVersion(snapshot1, "a.txt", null, "hash-v1", 10, now, FileChangeKind.Added, now);
        Repository.CompleteSnapshot(snapshot1, now, SnapshotStats.Empty);

        var snapshot2 = Repository.BeginSnapshot(now.AddMinutes(1));
        Repository.RecordFileVersion(snapshot2, "a.txt", null, "hash-v2", 20, now.AddMinutes(1), FileChangeKind.Changed, now.AddMinutes(1));
        Repository.CompleteSnapshot(snapshot2, now.AddMinutes(1), SnapshotStats.Empty);

        var current = Repository.GetCurrentState();

        var state = Assert.Single(current.Values);
        Assert.Equal("hash-v2", state.ContentHash);
        Assert.Equal(20, state.Size);
    }

    [Fact]
    public void GetCurrentState_matches_a_recorded_path_case_insensitively()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Repository.BeginSnapshot(now);
        Repository.RecordFileVersion(snapshot, "a.txt", null, "hash-v1", 10, now, FileChangeKind.Added, now);
        Repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);

        var current = Repository.GetCurrentState();

        Assert.Single(current);
        Assert.True(current.ContainsKey("A.TXT"));
        Assert.Equal("hash-v1", current["A.TXT"].ContentHash);
    }

    [Fact]
    public void GetCurrentState_resolves_multiple_recorded_casings_of_the_same_path_to_one_deterministic_row()
    {
        // Regression test for the original bug report: rows recorded for the same
        // logical path under two different casings (e.g. from before this capability
        // existed) must resolve to exactly one entry, deterministically the
        // most-recently-recorded row, rather than nondeterministically alternating.
        var now = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(now);
        Repository.RecordFileVersion(s1, "Photo.JPG", null, "hash-old-casing", 10, now, FileChangeKind.Added, now);
        Repository.CompleteSnapshot(s1, now, SnapshotStats.Empty);

        var s2 = Repository.BeginSnapshot(now.AddMinutes(1));
        Repository.RecordFileVersion(s2, "photo.jpg", null, "hash-new-casing", 20, now.AddMinutes(1), FileChangeKind.Added, now.AddMinutes(1));
        Repository.CompleteSnapshot(s2, now.AddMinutes(1), SnapshotStats.Empty);

        for (var i = 0; i < 5; i++)
        {
            var current = Repository.GetCurrentState();
            var state = Assert.Single(current.Values);
            Assert.Equal("hash-new-casing", state.ContentHash);
            Assert.Equal(20, state.Size);
        }
    }

    [Fact]
    public void GetCurrentState_excludes_deleted_paths()
    {
        var now = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(now);
        Repository.RecordFileVersion(s1, "a.txt", null, "hash-v1", 10, now, FileChangeKind.Added, now);
        Repository.CompleteSnapshot(s1, now, SnapshotStats.Empty);

        var s2 = Repository.BeginSnapshot(now.AddMinutes(1));
        Repository.RecordFileVersion(s2, "a.txt", null, "hash-v1", 10, now.AddMinutes(1), FileChangeKind.Deleted, now.AddMinutes(1));
        Repository.CompleteSnapshot(s2, now.AddMinutes(1), SnapshotStats.Empty);

        Assert.Empty(Repository.GetCurrentState());
    }

    [Fact]
    public void GetCurrentState_round_trips_a_recorded_quick_hash()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Repository.BeginSnapshot(now);
        Repository.RecordFileVersion(snapshot, "a.txt", null, "hash-v1", 10, now, FileChangeKind.Added, now, quickHash: "quick-v1", quickHashScheme: 1);
        Repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);

        var state = Repository.GetCurrentState()["a.txt"];

        Assert.Equal("quick-v1", state.QuickHash);
        Assert.Equal(1, state.QuickHashScheme);
    }

    [Fact]
    public void GetCurrentState_reports_no_quick_hash_when_none_was_recorded()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Repository.BeginSnapshot(now);
        Repository.RecordFileVersion(snapshot, "a.txt", null, "hash-v1", 10, now, FileChangeKind.Added, now);
        Repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);

        var state = Repository.GetCurrentState()["a.txt"];

        Assert.Null(state.QuickHash);
        Assert.Null(state.QuickHashScheme);
    }

    [Fact]
    public void GetStateAsOf_resolves_multiple_recorded_casings_of_the_same_path_to_one_deterministic_row()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "Photo.JPG", null, "hash-old-casing", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "photo.jpg", null, "hash-new-casing", 20, t1, FileChangeKind.Added, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        var asOf = t1.AddDays(1);
        for (var i = 0; i < 5; i++)
        {
            var current = Repository.GetStateAsOf(asOf);
            var state = Assert.Single(current.Values);
            Assert.Equal("hash-new-casing", state.ContentHash);
        }
    }

    [Fact]
    public void GetTombstones_resolves_multiple_recorded_casings_of_the_same_path_to_one_deterministic_row()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "Photo.JPG", null, "hash-old-casing", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "photo.jpg", null, "hash-new-casing", 20, t1, FileChangeKind.Deleted, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        for (var i = 0; i < 5; i++)
        {
            var tombstone = Assert.Single(Repository.GetTombstones(asOf: null));
            Assert.Equal("photo.jpg", tombstone.RelativePath);
        }
    }

    [Fact]
    public void GetMoveOrigins_resolves_multiple_recorded_casings_of_the_same_path_to_one_deterministic_row()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "old\\Photo.JPG", null, "hash-v1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "new\\Photo.JPG", "old\\Photo.JPG", "hash-v1", 10, t1, FileChangeKind.Moved, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        // A second, differently-cased row recorded for the same logical destination path.
        var t2 = t1.AddDays(1);
        var s3 = Repository.BeginSnapshot(t2);
        Repository.RecordFileVersion(s3, "new\\photo.jpg", "old\\Photo.JPG", "hash-v1", 10, t2, FileChangeKind.Moved, t2);
        Repository.CompleteSnapshot(s3, t2, SnapshotStats.Empty);

        for (var i = 0; i < 5; i++)
        {
            var origins = Repository.GetMoveOrigins();
            Assert.Single(origins);
            Assert.Equal("new\\photo.jpg", origins["old\\Photo.JPG"]);
        }
    }

    [Fact]
    public void GetStateAsOf_excludes_a_path_added_after_the_given_date()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, "hash-v1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var asOf = t0.AddMinutes(-1);

        Assert.Empty(Repository.GetStateAsOf(asOf));
    }

    [Fact]
    public void GetStateAsOf_returns_the_version_current_at_that_date_for_a_path_changed_both_before_and_after()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, "hash-v1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "a.txt", null, "hash-v2", 20, t1, FileChangeKind.Changed, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        var asOf = t0.AddHours(12);

        var state = Repository.GetStateAsOf(asOf)["a.txt"];
        Assert.Equal("hash-v1", state.ContentHash);
    }

    [Fact]
    public void GetStateAsOf_excludes_a_path_deleted_before_the_given_date()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, "hash-v1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "a.txt", null, "hash-v1", 10, t1, FileChangeKind.Deleted, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        var asOf = t1.AddDays(1);

        Assert.Empty(Repository.GetStateAsOf(asOf));
    }

    [Fact]
    public void GetTombstones_with_no_bound_returns_a_deleted_path()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, "hash-v1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "a.txt", null, "hash-v1", 10, t1, FileChangeKind.Deleted, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        var tombstone = Assert.Single(Repository.GetTombstones(asOf: null));
        Assert.Equal("a.txt", tombstone.RelativePath);
    }

    [Fact]
    public void GetTombstones_excludes_a_path_deleted_after_the_given_date()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, "hash-v1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "a.txt", null, "hash-v1", 10, t1, FileChangeKind.Deleted, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        Assert.Empty(Repository.GetTombstones(asOf: t0.AddHours(12)));
    }

    [Fact]
    public void GetTombstones_excludes_a_path_that_is_live_again_after_being_re_added_post_deletion()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, "hash-v1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "a.txt", null, "hash-v1", 10, t1, FileChangeKind.Deleted, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        var t2 = t1.AddDays(1);
        var s3 = Repository.BeginSnapshot(t2);
        Repository.RecordFileVersion(s3, "a.txt", null, "hash-v2", 12, t2, FileChangeKind.Added, t2);
        Repository.CompleteSnapshot(s3, t2, SnapshotStats.Empty);

        Assert.Empty(Repository.GetTombstones(asOf: null));
    }

    [Fact]
    public void GetMoveOrigins_maps_a_moved_paths_origin_to_its_current_location()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "old\\a.txt", null, "hash-v1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "new\\a.txt", "old\\a.txt", "hash-v1", 10, t1, FileChangeKind.Moved, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        var origins = Repository.GetMoveOrigins();

        Assert.Equal("new\\a.txt", origins["old\\a.txt"]);
        Assert.True(Repository.GetCurrentState().ContainsKey("new\\a.txt"));
    }

    [Fact]
    public void GetFileHistory_follows_a_move_chain_backward()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, @"Downloads\report.pdf", null, "hash-1", 100, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, @"Documents\report.pdf", @"Downloads\report.pdf", "hash-1", 100, t1, FileChangeKind.Moved, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        var history = Repository.GetFileHistory(@"Documents\report.pdf");

        Assert.Equal(2, history.Count);
        Assert.Equal(FileChangeKind.Moved, history[0].ChangeKind);
        Assert.Equal(FileChangeKind.Added, history[1].ChangeKind);
        Assert.Equal(@"Downloads\report.pdf", history[1].RelativePath);
    }

    [Fact]
    public void GetFileHistory_for_an_untracked_path_is_empty()
    {
        Assert.Empty(Repository.GetFileHistory("never-tracked.txt"));
    }

    [Fact]
    public void FindVersionAsOf_returns_the_version_current_at_that_time()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, "hash-v1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(10);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "a.txt", null, "hash-v2", 20, t1, FileChangeKind.Changed, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        var asOfBeforeChange = Repository.FindVersionAsOf("a.txt", t0.AddDays(5));
        var asOfAfterChange = Repository.FindVersionAsOf("a.txt", t1.AddDays(1));

        Assert.Equal("hash-v1", asOfBeforeChange?.ContentHash);
        Assert.Equal("hash-v2", asOfAfterChange?.ContentHash);
    }

    [Fact]
    public void FindVersionAsOf_after_deletion_returns_null()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, "hash-v1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "a.txt", null, "hash-v1", 10, t1, FileChangeKind.Deleted, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        Assert.Null(Repository.FindVersionAsOf("a.txt", t1.AddDays(1)));
    }

    [Fact]
    public void DeleteSnapshot_removes_the_snapshot_and_its_file_versions()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Repository.BeginSnapshot(now);
        Repository.RecordFileVersion(id, "a.txt", null, "hash-v1", 10, now, FileChangeKind.Added, now);
        Repository.CompleteSnapshot(id, now, SnapshotStats.Empty);

        Repository.DeleteSnapshot(id);

        Assert.Empty(Repository.ListSnapshots());
        Assert.Empty(Repository.GetFileHistory("a.txt"));
    }

    [Fact]
    public void GetAllReferencedContentHashes_returns_hashes_still_present_after_a_deletion()
    {
        var now = DateTimeOffset.UtcNow;

        // A snapshot that stays around, referencing content that should remain "kept".
        var keeper = Repository.BeginSnapshot(now);
        Repository.RecordFileVersion(keeper, "a.txt", null, "hash-keep", 10, now, FileChangeKind.Added, now);
        Repository.CompleteSnapshot(keeper, now, SnapshotStats.Empty);

        // A file that was added and later deleted; both snapshots referencing its
        // content get pruned away, so the content becomes fully unreferenced.
        var s1 = Repository.BeginSnapshot(now.AddMinutes(1));
        Repository.RecordFileVersion(s1, "b.txt", null, "hash-gone", 20, now.AddMinutes(1), FileChangeKind.Added, now.AddMinutes(1));
        Repository.CompleteSnapshot(s1, now.AddMinutes(1), SnapshotStats.Empty);

        var s2 = Repository.BeginSnapshot(now.AddMinutes(2));
        Repository.RecordFileVersion(s2, "b.txt", null, "hash-gone", 20, now.AddMinutes(2), FileChangeKind.Deleted, now.AddMinutes(2));
        Repository.CompleteSnapshot(s2, now.AddMinutes(2), SnapshotStats.Empty);

        // Simulate pruning: both snapshots referencing "hash-gone" have expired and are removed.
        Repository.DeleteSnapshot(s1);
        Repository.DeleteSnapshot(s2);

        var referenced = Repository.GetAllReferencedContentHashes();

        Assert.Contains("hash-keep", referenced);
        Assert.DoesNotContain("hash-gone", referenced);
    }

    [Fact]
    public void PruneSnapshots_preserves_the_current_row_for_a_still_live_path_even_if_its_snapshot_is_pruned()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var onlySnapshot = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(onlySnapshot, "unchanged.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(onlySnapshot, t0, SnapshotStats.Empty);

        // The only snapshot mentioning "unchanged.txt" is now "expired" per retention,
        // even though the file itself hasn't changed and is still live.
        var removedCount = Repository.PruneSnapshots([onlySnapshot]);

        Assert.Equal(0, removedCount); // the snapshot is kept because its row is still current
        Assert.True(Repository.GetCurrentState().ContainsKey("unchanged.txt"));
        Assert.Single(Repository.ListSnapshots());
    }

    [Fact]
    public void PruneSnapshots_removes_a_snapshot_entirely_once_none_of_its_rows_are_current()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var oldSnapshot = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(oldSnapshot, "a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(oldSnapshot, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var newSnapshot = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(newSnapshot, "a.txt", null, "hash-2", 20, t1, FileChangeKind.Changed, t1);
        Repository.CompleteSnapshot(newSnapshot, t1, SnapshotStats.Empty);

        // The old snapshot's row for "a.txt" is now superseded by the new snapshot's row.
        var removedCount = Repository.PruneSnapshots([oldSnapshot]);

        Assert.Equal(1, removedCount);
        Assert.DoesNotContain(oldSnapshot, Repository.ListSnapshots().Select(s => s.Id));
        Assert.Equal("hash-2", Repository.GetCurrentState()["a.txt"].ContentHash);
    }

    [Fact]
    public void A_fresh_database_uses_wal_journal_mode()
    {
        _ = Repository; // trigger creation

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = (string)command.ExecuteScalar()!;

        // WAL mode is persisted in the database file's header, so any connection that
        // opens the file - not just the one that requested it - reports "wal".
        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public void Manifest_batch_writes_are_not_durable_until_committed()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshotId = Repository.BeginSnapshot(now);

        using (var batch = Repository.BeginManifestBatch())
        {
            Repository.RecordFileVersion(snapshotId, "a.txt", null, "hash-1", 10, now, FileChangeKind.Added, now);

            Assert.Equal(0, CountFileVersionRowsViaSeparateConnection());

            batch.Commit();
        }

        Assert.Equal(1, CountFileVersionRowsViaSeparateConnection());
    }

    [Fact]
    public void A_batch_disposed_without_commit_rolls_back_both_rows_and_snapshot_outcome()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshotId = Repository.BeginSnapshot(now);

        using (var batch = Repository.BeginManifestBatch())
        {
            Repository.RecordFileVersion(snapshotId, "a.txt", null, "hash-1", 10, now, FileChangeKind.Added, now);
            Repository.CompleteSnapshot(snapshotId, now, new SnapshotStats(10, 1, 0, 0, 0, 0));
            // No Commit() call - models a crash mid-batch: the rows and the outcome
            // update above must both roll back together (design.md's atomic-outcome decision).
        }

        // A fresh repository instance opening the same file models the process restarting.
        using var reopened = new SqliteSnapshotRepository(_dbPath);
        reopened.ReconcileIncompleteSnapshots();

        var snapshot = reopened.ListSnapshots().Single(s => s.Id == snapshotId);
        Assert.Equal(SnapshotStatus.Failed, snapshot.Status); // reconciled from the still-Running state
        Assert.Empty(reopened.GetFileHistory("a.txt"));
    }

    [Fact]
    public void Committing_a_batch_checkpoints_and_truncates_the_wal_file()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshotId = Repository.BeginSnapshot(now);

        using (var batch = Repository.BeginManifestBatch())
        {
            Repository.RecordFileVersion(snapshotId, "a.txt", null, "hash-1", 10, now, FileChangeKind.Added, now);
            Repository.CompleteSnapshot(snapshotId, now, new SnapshotStats(10, 1, 0, 0, 0, 0));
            batch.Commit();
        }

        var walPath = _dbPath + "-wal";
        var walSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        Assert.Equal(0, walSize);
    }

    [Fact]
    public void Beginning_a_second_batch_while_one_is_active_throws()
    {
        Repository.BeginSnapshot(DateTimeOffset.UtcNow);
        using var batch = Repository.BeginManifestBatch();

        Assert.Throws<InvalidOperationException>(() => Repository.BeginManifestBatch());
    }

    private int CountFileVersionRowsViaSeparateConnection()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM file_versions";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // fix-readonly-command-side-effects: createIfMissing behavior.

    [Fact]
    public void Default_constructor_still_eagerly_creates_the_database_file()
    {
        Assert.False(File.Exists(_dbPath));

        using var repository = new SqliteSnapshotRepository(_dbPath);

        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public void CreateIfMissing_false_does_not_create_the_database_file_when_absent()
    {
        Assert.False(File.Exists(_dbPath));

        using var repository = new SqliteSnapshotRepository(_dbPath, createIfMissing: false);

        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public void CreateIfMissing_false_reports_no_recorded_state_when_the_database_is_absent()
    {
        using var repository = new SqliteSnapshotRepository(_dbPath, createIfMissing: false);

        Assert.Empty(repository.ListSnapshots());
        Assert.Null(repository.GetLastCompletedSnapshot());
        Assert.Empty(repository.GetCurrentState());
        Assert.Empty(repository.GetStateAsOf(DateTimeOffset.UtcNow));
        Assert.Empty(repository.GetTombstones(null));
        Assert.Empty(repository.GetMoveOrigins());
        Assert.Empty(repository.GetFileHistory("a.txt"));
        Assert.Null(repository.FindVersionAsOf("a.txt", DateTimeOffset.UtcNow));
        Assert.Empty(repository.GetAllReferencedContentHashes());
    }

    [Fact]
    public void CreateIfMissing_false_throws_on_a_write_member_when_the_database_is_absent()
    {
        using var repository = new SqliteSnapshotRepository(_dbPath, createIfMissing: false);

        Assert.Throws<InvalidOperationException>(() => repository.BeginSnapshot(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CreateIfMissing_false_still_opens_normally_when_the_database_already_exists()
    {
        using (var seed = new SqliteSnapshotRepository(_dbPath))
        {
            var snapshotId = seed.BeginSnapshot(DateTimeOffset.UtcNow);
            seed.CompleteSnapshot(snapshotId, DateTimeOffset.UtcNow, SnapshotStats.Empty);
        }

        SqliteConnection.ClearAllPools();
        using var repository = new SqliteSnapshotRepository(_dbPath, createIfMissing: false);

        Assert.Single(repository.ListSnapshots());
    }
}
