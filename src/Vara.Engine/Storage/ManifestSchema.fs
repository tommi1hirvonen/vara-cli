namespace Vara.Engine

open System
open Microsoft.Data.Sqlite

/// The manifest's tables and the migrations that bring an older database to them.
module internal ManifestSchema =
    open ManifestSql

    let private createTables =
        """
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
            content_hash TEXT NULL,
            size INTEGER NOT NULL,
            source_modified_at TEXT NOT NULL,
            change_kind TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            quick_hash TEXT NULL,
            quick_hash_scheme INTEGER NULL,
            link_target TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_file_versions_relative_path ON file_versions(relative_path, id);
        CREATE INDEX IF NOT EXISTS idx_file_versions_snapshot_id ON file_versions(snapshot_id);
        CREATE INDEX IF NOT EXISTS idx_file_versions_content_hash ON file_versions(content_hash);
        """

    // Written out in full, not built from `createTables`: a migration is a record of what the database was changed
    // to at the time, and must not change when the current tables do.

    /// Recreates `file_versions` with `COLLATE NOCASE` on the path and copies every row across unchanged. SQLite
    /// cannot alter a column's collation, and a database from before the fix lacks it. Guarded by the table's own
    /// recorded schema, so it does nothing for a current or a new database.
    let private migrateCollation =
        """
        CREATE TABLE file_versions_migrated (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
            relative_path TEXT NOT NULL COLLATE NOCASE,
            previous_relative_path TEXT NULL,
            content_hash TEXT NULL,
            size INTEGER NOT NULL,
            source_modified_at TEXT NOT NULL,
            change_kind TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            quick_hash TEXT NULL,
            quick_hash_scheme INTEGER NULL,
            link_target TEXT NULL
        );

        INSERT INTO file_versions_migrated
            (id, snapshot_id, relative_path, previous_relative_path, content_hash,
             size, source_modified_at, change_kind, recorded_at, quick_hash,
             quick_hash_scheme, link_target)
        SELECT id, snapshot_id, relative_path, previous_relative_path, content_hash,
               size, source_modified_at, change_kind, recorded_at, quick_hash,
               quick_hash_scheme, NULL
        FROM file_versions;

        DROP TABLE file_versions;
        ALTER TABLE file_versions_migrated RENAME TO file_versions;

        UPDATE sqlite_sequence SET seq = (SELECT COALESCE(MAX(id), 0) FROM file_versions) WHERE name = 'file_versions';

        CREATE INDEX IF NOT EXISTS idx_file_versions_relative_path ON file_versions(relative_path, id);
        CREATE INDEX IF NOT EXISTS idx_file_versions_snapshot_id ON file_versions(snapshot_id);
        CREATE INDEX IF NOT EXISTS idx_file_versions_content_hash ON file_versions(content_hash);
        """

    let private migrateCollationIfNeeded (connection: SqliteConnection) : unit =
        let tableSql =
            use check = connection.CreateCommand()
            check.CommandText <- "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'file_versions'"

            match check.ExecuteScalar() with
            | :? string as text -> ValueSome text
            | _ -> ValueNone

        match tableSql with
        | ValueSome text when not (text.Contains("COLLATE NOCASE", StringComparison.OrdinalIgnoreCase)) ->
            use transaction = connection.BeginTransaction()
            execute connection transaction migrateCollation
            transaction.Commit()
        | _ -> ()

    /// Prepares a freshly opened writable connection: journal mode, the tables, and any migration.
    let initialize (connection: SqliteConnection) : unit =
        // WAL lets the single writer commit without locking the file, and NORMAL skips an fsync per commit while
        // never corrupting the database: only a very recent commit can be lost in a power failure.
        execute connection null "PRAGMA journal_mode = 'wal'; PRAGMA synchronous = 'normal';"
        execute connection null createTables
        migrateCollationIfNeeded connection
