using System.Globalization;
using Microsoft.Data.Sqlite;
using Vara.Core.Abstractions;
using Vara.Core.Snapshots;

namespace Vara.Infrastructure.Snapshots;

/// <summary>
/// SQLite-backed manifest for a single profile: snapshots and their file-version
/// history. Uses <c>Microsoft.Data.Sqlite</c> directly (no ORM) - see design.md for
/// the rationale (small, stable schema; set-based queries; Native AOT safety).
/// Opens with <c>journal_mode=WAL</c>/<c>synchronous=NORMAL</c> and supports an
/// explicit <see cref="BeginManifestBatch"/> scope so a caller can group many
/// <see cref="RecordFileVersion"/> (and outcome) writes into a bounded number of
/// periodic checkpoint commits instead of paying a fsync per call - see the
/// batch-manifest-writes change's design.md and the add-backup-checkpoints-and-cancellation
/// change's design.md for the periodic-checkpoint follow-up.
/// </summary>
public sealed class SqliteSnapshotRepository : ISnapshotRepository
{
    private readonly SqliteConnection? _connection;
    private SqliteTransaction? _activeBatchTransaction;

    /// <summary>
    /// Opens (or creates) the manifest at <paramref name="databasePath"/>. When
    /// <paramref name="createIfMissing"/> is <see langword="false"/> and no database file
    /// already exists there, no directory or file is created - the repository instead
    /// starts in an "empty" mode where every read member reports the profile has no
    /// recorded state (matching what a freshly-created, never-written-to database would
    /// report), and any write member throws <see cref="InvalidOperationException"/>. This
    /// lets read-only callers (see the fix-readonly-command-side-effects change) resolve a
    /// repository for a profile that has never completed a backup run without creating
    /// <c>.vara\profile.db</c> as a side effect.
    /// </summary>
    public SqliteSnapshotRepository(string databasePath, bool createIfMissing = true)
    {
        if (!createIfMissing && !File.Exists(databasePath))
        {
            _connection = null;
            return;
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        ConfigurePragmas(_connection);
        InitializeSchema(_connection);
    }

    /// <summary>
    /// The open connection, for members that require a real (non-empty-mode) repository.
    /// Throws if this repository was constructed with <c>createIfMissing: false</c> against
    /// a profile with no recorded state - no read-only caller should ever reach a write
    /// member in that case.
    /// </summary>
    private SqliteConnection RequireConnection() => _connection
        ?? throw new InvalidOperationException(
            "This profile has no recorded backup state, so its manifest cannot be written to " +
            "without first running a backup.");

    /// <summary>
    /// WAL journaling lets the single writer commit without exclusively locking the
    /// whole file, and <c>synchronous=NORMAL</c> avoids an fsync on every commit while
    /// still guaranteeing the database itself is never corrupted on crash - only that a
    /// very recent commit might not survive a power loss, which the batch commit
    /// boundary already tolerates a wider exposure than (see design.md's PRAGMA
    /// decision). WAL mode persists in the database file itself, so this only needs to
    /// be requested once; <c>synchronous</c> is connection-scoped and is set every open.
    /// </summary>
    private static void ConfigurePragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = 'wal'; PRAGMA synchronous = 'normal';";
        command.ExecuteNonQuery();
    }

    private void InitializeSchema(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS snapshots (
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

                CREATE TABLE IF NOT EXISTS file_versions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
                    relative_path TEXT NOT NULL COLLATE NOCASE,
                    previous_relative_path TEXT NULL,
                    content_hash TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    source_modified_at TEXT NOT NULL,
                    change_kind TEXT NOT NULL,
                    recorded_at TEXT NOT NULL,
                    quick_hash TEXT NULL,
                    quick_hash_scheme INTEGER NULL
                );

                CREATE INDEX IF NOT EXISTS idx_file_versions_relative_path ON file_versions(relative_path, id);
                CREATE INDEX IF NOT EXISTS idx_file_versions_snapshot_id ON file_versions(snapshot_id);
                CREATE INDEX IF NOT EXISTS idx_file_versions_content_hash ON file_versions(content_hash);
                """;
            command.ExecuteNonQuery();
        }

        MigrateFileVersionsCollationIfNeeded(connection);
    }

    /// <summary>
    /// A <c>profile.db</c> created before this fix has <c>file_versions</c> without
    /// <c>COLLATE NOCASE</c> on <c>relative_path</c> - the <c>CREATE TABLE IF NOT EXISTS</c>
    /// above is a no-op for it, since SQLite cannot alter a column's collation in place.
    /// This recreates the table with the corrected collation, copies every existing row
    /// across unchanged, and swaps it in. Guarded by inspecting the table's actual
    /// recorded schema (via <c>sqlite_master</c>) rather than a separate version counter,
    /// so it is a no-op - and safe to call on every startup - for already-migrated or
    /// brand-new databases.
    /// </summary>
    private static void MigrateFileVersionsCollationIfNeeded(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'file_versions'";
            var tableSql = check.ExecuteScalar() as string;
            if (tableSql is null || tableSql.Contains("COLLATE NOCASE", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var transaction = connection.BeginTransaction();
        using (var migrate = connection.CreateCommand())
        {
            migrate.Transaction = transaction;
            migrate.CommandText = """
                CREATE TABLE file_versions_migrated (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
                    relative_path TEXT NOT NULL COLLATE NOCASE,
                    previous_relative_path TEXT NULL,
                    content_hash TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    source_modified_at TEXT NOT NULL,
                    change_kind TEXT NOT NULL,
                    recorded_at TEXT NOT NULL,
                    quick_hash TEXT NULL,
                    quick_hash_scheme INTEGER NULL
                );

                INSERT INTO file_versions_migrated
                    (id, snapshot_id, relative_path, previous_relative_path, content_hash, size, source_modified_at, change_kind, recorded_at, quick_hash, quick_hash_scheme)
                SELECT id, snapshot_id, relative_path, previous_relative_path, content_hash, size, source_modified_at, change_kind, recorded_at, quick_hash, quick_hash_scheme
                FROM file_versions;

                DROP TABLE file_versions;
                ALTER TABLE file_versions_migrated RENAME TO file_versions;

                UPDATE sqlite_sequence SET seq = (SELECT COALESCE(MAX(id), 0) FROM file_versions) WHERE name = 'file_versions';

                CREATE INDEX IF NOT EXISTS idx_file_versions_relative_path ON file_versions(relative_path, id);
                CREATE INDEX IF NOT EXISTS idx_file_versions_snapshot_id ON file_versions(snapshot_id);
                CREATE INDEX IF NOT EXISTS idx_file_versions_content_hash ON file_versions(content_hash);
                """;
            migrate.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Dispose() => _connection?.Dispose();

    public void ReconcileIncompleteSnapshots()
    {
        using var command = RequireConnection().CreateCommand();
        command.CommandText = """
            UPDATE snapshots
            SET status = $failed, completed_at = COALESCE(completed_at, $now)
            WHERE status = $running
            """;
        command.Parameters.AddWithValue("$failed", nameof(SnapshotStatus.Failed));
        command.Parameters.AddWithValue("$running", nameof(SnapshotStatus.Running));
        command.Parameters.AddWithValue("$now", ToIso(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    public long BeginSnapshot(DateTimeOffset startedAt)
    {
        using var command = RequireConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO snapshots (started_at, status) VALUES ($startedAt, $status);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$startedAt", ToIso(startedAt));
        command.Parameters.AddWithValue("$status", nameof(SnapshotStatus.Running));
        return (long)command.ExecuteScalar()!;
    }

    public void RecordFileVersion(
        long snapshotId,
        string relativePath,
        string? previousRelativePath,
        string contentHash,
        long size,
        DateTimeOffset sourceModifiedAt,
        FileChangeKind changeKind,
        DateTimeOffset recordedAt,
        string? quickHash = null,
        int? quickHashScheme = null)
    {
        using var command = RequireConnection().CreateCommand();
        command.Transaction = _activeBatchTransaction;
        command.CommandText = """
            INSERT INTO file_versions
                (snapshot_id, relative_path, previous_relative_path, content_hash, size, source_modified_at, change_kind, recorded_at, quick_hash, quick_hash_scheme)
            VALUES
                ($snapshotId, $relativePath, $previousPath, $hash, $size, $sourceModifiedAt, $changeKind, $recordedAt, $quickHash, $quickHashScheme)
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$relativePath", relativePath);
        command.Parameters.AddWithValue("$previousPath", (object?)previousRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", contentHash);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$sourceModifiedAt", ToIso(sourceModifiedAt));
        command.Parameters.AddWithValue("$changeKind", changeKind.ToString());
        command.Parameters.AddWithValue("$recordedAt", ToIso(recordedAt));
        command.Parameters.AddWithValue("$quickHash", (object?)quickHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$quickHashScheme", (object?)quickHashScheme ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void CompleteSnapshot(long snapshotId, DateTimeOffset completedAt, SnapshotStats stats) =>
        UpdateSnapshotOutcome(snapshotId, completedAt, SnapshotStatus.Complete, stats);

    public void FailSnapshot(long snapshotId, DateTimeOffset failedAt, SnapshotStats stats) =>
        UpdateSnapshotOutcome(snapshotId, failedAt, SnapshotStatus.Failed, stats);

    public void CancelSnapshot(long snapshotId, DateTimeOffset cancelledAt, SnapshotStats stats) =>
        UpdateSnapshotOutcome(snapshotId, cancelledAt, SnapshotStatus.Cancelled, stats);

    /// <summary>
    /// See <see cref="ISnapshotRepository.BeginManifestBatch"/>. Backed by a real SQLite
    /// transaction: <see cref="RecordFileVersion"/> and <see cref="UpdateSnapshotOutcome"/>
    /// attach to it via <see cref="_activeBatchTransaction"/> for as long as it is open, so
    /// they commit (or roll back) together instead of one auto-commit per call.
    /// </summary>
    public IManifestBatch BeginManifestBatch()
    {
        if (_activeBatchTransaction is not null)
        {
            throw new InvalidOperationException("A manifest batch is already active on this repository.");
        }

        _activeBatchTransaction = RequireConnection().BeginTransaction();
        return new ManifestBatch(this);
    }

    /// <summary>
    /// Commits the active transaction as a checkpoint, then immediately reopens a fresh
    /// one on the same connection so subsequent writes keep attaching to an active
    /// transaction - each call is a checkpoint, not necessarily the batch's final commit
    /// (periodic-checkpointing follow-up to the original batch-manifest-writes change;
    /// see design.md).
    /// </summary>
    private void CheckpointActiveBatch()
    {
        if (_activeBatchTransaction is null)
        {
            throw new InvalidOperationException("No active manifest batch to commit.");
        }

        _activeBatchTransaction.Commit();
        _activeBatchTransaction.Dispose();

        // Fold the just-committed writes back into the main database file so a plain
        // copy of it (without the -wal/-shm sidecar files) is self-contained again
        // between runs - see design.md's WAL-checkpoint decision.
        using (var checkpoint = RequireConnection().CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }

        _activeBatchTransaction = RequireConnection().BeginTransaction();
    }

    private void DiscardActiveBatch()
    {
        if (_activeBatchTransaction is null)
        {
            return;
        }

        _activeBatchTransaction.Rollback();
        _activeBatchTransaction.Dispose();
        _activeBatchTransaction = null;
    }

    /// <summary>
    /// Disposing without a final <see cref="Commit"/> rolls back only the transaction
    /// currently active (i.e. the writes made since the most recent checkpoint, or since
    /// the batch began if none has committed yet) - earlier checkpoints already committed
    /// durably and are unaffected. Models (or recovers cleanly from) an interruption per
    /// the backup-execution spec's periodic-checkpointing requirement.
    /// </summary>
    private sealed class ManifestBatch(SqliteSnapshotRepository owner) : IManifestBatch
    {
        private bool _disposed;

        public void Commit()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner.CheckpointActiveBatch();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            owner.DiscardActiveBatch();
            _disposed = true;
        }
    }

    private void UpdateSnapshotOutcome(long snapshotId, DateTimeOffset completedAt, SnapshotStatus status, SnapshotStats stats)
    {
        using var command = RequireConnection().CreateCommand();
        command.Transaction = _activeBatchTransaction;
        command.CommandText = """
            UPDATE snapshots
            SET completed_at = $completedAt, status = $status,
                bytes_transferred = $bytes, files_added = $added, files_changed = $changed,
                files_moved = $moved, files_deleted = $deleted, files_failed = $failed
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$completedAt", ToIso(completedAt));
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$bytes", stats.BytesTransferred);
        command.Parameters.AddWithValue("$added", stats.FilesAdded);
        command.Parameters.AddWithValue("$changed", stats.FilesChanged);
        command.Parameters.AddWithValue("$moved", stats.FilesMoved);
        command.Parameters.AddWithValue("$deleted", stats.FilesDeleted);
        command.Parameters.AddWithValue("$failed", stats.FilesFailed);
        command.Parameters.AddWithValue("$id", snapshotId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, CurrentFileState> GetCurrentState()
    {
        if (_connection is null)
        {
            return new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase);
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT fv.relative_path, fv.content_hash, fv.size, fv.source_modified_at, fv.quick_hash, fv.quick_hash_scheme, fv.change_kind
            FROM file_versions fv
            INNER JOIN (
                SELECT relative_path, MAX(id) AS max_id
                FROM file_versions
                GROUP BY relative_path
            ) latest ON fv.relative_path = latest.relative_path AND fv.id = latest.max_id
            WHERE fv.change_kind != $deleted
            """;
        command.Parameters.AddWithValue("$deleted", nameof(FileChangeKind.Deleted));

        var result = new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relativePath = reader.GetString(0);
            result[relativePath] = new CurrentFileState(
                relativePath,
                reader.GetString(1),
                reader.GetInt64(2),
                ParseIso(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetString(6) == nameof(FileChangeKind.Linked));
        }

        return result;
    }

    public IReadOnlyDictionary<string, CurrentFileState> GetStateAsOf(DateTimeOffset asOf)
    {
        if (_connection is null)
        {
            return new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase);
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT fv.relative_path, fv.content_hash, fv.size, fv.source_modified_at, fv.quick_hash, fv.quick_hash_scheme, fv.change_kind
            FROM file_versions fv
            INNER JOIN (
                SELECT relative_path, MAX(id) AS max_id
                FROM file_versions
                WHERE recorded_at <= $asOf
                GROUP BY relative_path
            ) latest ON fv.relative_path = latest.relative_path AND fv.id = latest.max_id
            WHERE fv.change_kind != $deleted
            """;
        command.Parameters.AddWithValue("$asOf", ToIso(asOf));
        command.Parameters.AddWithValue("$deleted", nameof(FileChangeKind.Deleted));

        var result = new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relativePath = reader.GetString(0);
            result[relativePath] = new CurrentFileState(
                relativePath,
                reader.GetString(1),
                reader.GetInt64(2),
                ParseIso(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetString(6) == nameof(FileChangeKind.Linked));
        }

        return result;
    }

    public IReadOnlyList<FileVersionRecord> GetTombstones(DateTimeOffset? asOf)
    {
        if (_connection is null)
        {
            return [];
        }

        using var command = _connection.CreateCommand();
        command.CommandText = asOf is null
            ? """
              SELECT fv.id, fv.snapshot_id, fv.relative_path, fv.previous_relative_path, fv.content_hash, fv.size, fv.source_modified_at, fv.change_kind, fv.recorded_at, fv.quick_hash, fv.quick_hash_scheme
              FROM file_versions fv
              INNER JOIN (
                  SELECT relative_path, MAX(id) AS max_id
                  FROM file_versions
                  GROUP BY relative_path
              ) latest ON fv.relative_path = latest.relative_path AND fv.id = latest.max_id
              WHERE fv.change_kind = $deleted
              """
            : """
              SELECT fv.id, fv.snapshot_id, fv.relative_path, fv.previous_relative_path, fv.content_hash, fv.size, fv.source_modified_at, fv.change_kind, fv.recorded_at, fv.quick_hash, fv.quick_hash_scheme
              FROM file_versions fv
              INNER JOIN (
                  SELECT relative_path, MAX(id) AS max_id
                  FROM file_versions
                  WHERE recorded_at <= $asOf
                  GROUP BY relative_path
              ) latest ON fv.relative_path = latest.relative_path AND fv.id = latest.max_id
              WHERE fv.change_kind = $deleted
              """;
        command.Parameters.AddWithValue("$deleted", nameof(FileChangeKind.Deleted));
        if (asOf is not null)
        {
            command.Parameters.AddWithValue("$asOf", ToIso(asOf.Value));
        }

        var results = new List<FileVersionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadFileVersionRecord(reader));
        }

        return results;
    }

    public IReadOnlyDictionary<string, string> GetMoveOrigins()
    {
        if (_connection is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT fv.relative_path, fv.previous_relative_path
            FROM file_versions fv
            INNER JOIN (
                SELECT relative_path, MAX(id) AS max_id
                FROM file_versions
                GROUP BY relative_path
            ) latest ON fv.relative_path = latest.relative_path AND fv.id = latest.max_id
            WHERE fv.change_kind = $moved AND fv.previous_relative_path IS NOT NULL
            """;
        command.Parameters.AddWithValue("$moved", nameof(FileChangeKind.Moved));

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var currentPath = reader.GetString(0);
            var previousPath = reader.GetString(1);
            result[previousPath] = currentPath;
        }

        return result;
    }

    public IReadOnlyList<Snapshot> ListSnapshots()
    {
        if (_connection is null)
        {
            return [];
        }

        using var command = _connection.CreateCommand();
        command.CommandText = $"{SnapshotColumns} FROM snapshots ORDER BY id DESC";

        var results = new List<Snapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadSnapshot(reader));
        }

        return results;
    }

    public Snapshot? GetLastCompletedSnapshot()
    {
        if (_connection is null)
        {
            return null;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = $"{SnapshotColumns} FROM snapshots WHERE status = $complete ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$complete", nameof(SnapshotStatus.Complete));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSnapshot(reader) : null;
    }

    /// <summary>
    /// Returns the full recorded history of <paramref name="relativePath"/>, most recent
    /// first. Follows a path's move chain backward through <c>previous_relative_path</c> so
    /// that a file's history survives being relocated, per the backup-execution spec's
    /// "version history carries forward" requirement for moves.
    /// </summary>
    public IReadOnlyList<FileVersionRecord> GetFileHistory(string relativePath)
    {
        var all = new List<FileVersionRecord>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? currentPath = relativePath;

        while (currentPath is not null && visited.Add(currentPath))
        {
            var rowsForPath = QueryRowsForPath(currentPath);
            all.AddRange(rowsForPath);

            // Rows are ordered most-recent-first; the last element is the earliest one
            // recorded under this path. If that earliest row is itself a move, the
            // path's history continues under whatever path it was moved from.
            var earliest = rowsForPath.Count > 0 ? rowsForPath[^1] : null;
            currentPath = earliest is { ChangeKind: FileChangeKind.Moved } ? earliest.PreviousRelativePath : null;
        }

        return all.OrderByDescending(r => r.Id).ToList();
    }

    public FileVersionRecord? FindVersionAsOf(string relativePath, DateTimeOffset asOf)
    {
        var history = GetFileHistory(relativePath);
        var match = history.FirstOrDefault(r => r.RecordedAt <= asOf);
        return match is { ChangeKind: FileChangeKind.Deleted } ? null : match;
    }

    public void DeleteSnapshot(long snapshotId)
    {
        var connection = RequireConnection();
        using var transaction = connection.BeginTransaction();

        using (var deleteVersions = connection.CreateCommand())
        {
            deleteVersions.Transaction = transaction;
            deleteVersions.CommandText = "DELETE FROM file_versions WHERE snapshot_id = $id";
            deleteVersions.Parameters.AddWithValue("$id", snapshotId);
            deleteVersions.ExecuteNonQuery();
        }

        using (var deleteSnapshot = connection.CreateCommand())
        {
            deleteSnapshot.Transaction = transaction;
            deleteSnapshot.CommandText = "DELETE FROM snapshots WHERE id = $id";
            deleteSnapshot.Parameters.AddWithValue("$id", snapshotId);
            deleteSnapshot.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public int PruneSnapshots(IReadOnlyList<long> snapshotIds)
    {
        if (snapshotIds.Count == 0)
        {
            return 0;
        }

        var connection = RequireConnection();
        var idList = string.Join(",", snapshotIds);
        using var transaction = connection.BeginTransaction();

        // Delete only rows that are NOT the current (latest, non-deleted) row for their
        // path - a row still representing live state is preserved even if its snapshot
        // is otherwise expired, so a file that hasn't changed recently is never silently
        // dropped from the current-state view. The "current" row must be the true
        // MAX(id) for its path across ALL rows (including deletion tombstones) - only
        // then, if that row itself isn't a deletion, is it protected. Excluding deleted
        // rows before computing MAX(id) would wrongly protect a stale pre-deletion row.
        using (var deleteRows = connection.CreateCommand())
        {
            deleteRows.Transaction = transaction;
            deleteRows.CommandText = $"""
                DELETE FROM file_versions
                WHERE snapshot_id IN ({idList})
                  AND id NOT IN (
                      SELECT fv.id
                      FROM file_versions fv
                      INNER JOIN (
                          SELECT relative_path, MAX(id) AS max_id FROM file_versions GROUP BY relative_path
                      ) latest ON fv.relative_path = latest.relative_path AND fv.id = latest.max_id
                      WHERE fv.change_kind != $deleted
                  )
                """;
            deleteRows.Parameters.AddWithValue("$deleted", nameof(FileChangeKind.Deleted));
            deleteRows.ExecuteNonQuery();
        }

        int removedCount;
        using (var deleteSnapshots = connection.CreateCommand())
        {
            deleteSnapshots.Transaction = transaction;
            deleteSnapshots.CommandText = $"""
                DELETE FROM snapshots
                WHERE id IN ({idList})
                  AND id NOT IN (SELECT DISTINCT snapshot_id FROM file_versions);
                SELECT changes();
                """;
            removedCount = Convert.ToInt32(deleteSnapshots.ExecuteScalar());
        }

        transaction.Commit();
        return removedCount;
    }

    public IReadOnlySet<string> GetAllReferencedContentHashes()
    {
        if (_connection is null)
        {
            return new HashSet<string>();
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT content_hash FROM file_versions";

        var result = new HashSet<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private List<FileVersionRecord> QueryRowsForPath(string relativePath)
    {
        if (_connection is null)
        {
            return [];
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, snapshot_id, relative_path, previous_relative_path, content_hash, size, source_modified_at, change_kind, recorded_at, quick_hash, quick_hash_scheme
            FROM file_versions
            WHERE relative_path = $path
            ORDER BY id DESC
            """;
        command.Parameters.AddWithValue("$path", relativePath);

        var results = new List<FileVersionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadFileVersionRecord(reader));
        }

        return results;
    }

    private const string SnapshotColumns =
        "SELECT id, started_at, completed_at, status, bytes_transferred, files_added, files_changed, files_moved, files_deleted, files_failed";

    private static Snapshot ReadSnapshot(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        ParseIso(reader.GetString(1)),
        reader.IsDBNull(2) ? null : ParseIso(reader.GetString(2)),
        Enum.Parse<SnapshotStatus>(reader.GetString(3)),
        new SnapshotStats(
            reader.GetInt64(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9)));

    private static FileVersionRecord ReadFileVersionRecord(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        SnapshotId: reader.GetInt64(1),
        RelativePath: reader.GetString(2),
        PreviousRelativePath: reader.IsDBNull(3) ? null : reader.GetString(3),
        ContentHash: reader.GetString(4),
        Size: reader.GetInt64(5),
        SourceModifiedAt: ParseIso(reader.GetString(6)),
        ChangeKind: Enum.Parse<FileChangeKind>(reader.GetString(7)),
        RecordedAt: ParseIso(reader.GetString(8)),
        QuickHash: reader.IsDBNull(9) ? null : reader.GetString(9),
        QuickHashScheme: reader.IsDBNull(10) ? null : reader.GetInt32(10));

    private static string ToIso(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
