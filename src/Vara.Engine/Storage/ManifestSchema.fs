namespace Vara.Engine

open System
open System.Globalization
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

    let private migrateCollationIfNeeded (connection: SqliteConnection) (transaction: SqliteTransaction) : unit =
        let tableSql =
            use check = connection.CreateCommand()
            check.Transaction <- transaction
            check.CommandText <- "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'file_versions'"

            match check.ExecuteScalar() with
            | :? string as text -> ValueSome text
            | _ -> ValueNone

        match tableSql with
        | ValueSome text when not (text.Contains("COLLATE NOCASE", StringComparison.OrdinalIgnoreCase)) ->
            execute connection transaction migrateCollation
        | _ -> ()

    /// The schema version this build writes and the highest it can open. A database with tables and no recorded
    /// version is version 1: that is what every database written before versioning looks like.
    let currentVersion: int = 1

    /// The migrations from one version to the next, as (version it applies to, script). Each is written out in full,
    /// and runs in a transaction with the version bump that follows it. None exists yet.
    let private steps: (int * string) list = []

    let private scalar (connection: SqliteConnection) (transaction: SqliteTransaction | null) (sql: string) : int64 =
        use command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- sql
        Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture)

    /// The recorded schema version, 0 when none is recorded (a new file reads the same).
    let readVersion (connection: SqliteConnection) : int =
        int (scalar connection null "PRAGMA user_version;")

    /// Raises when the database is newer than `supported`. Reads only, so it is safe on a read-only connection.
    let refuseNewerThan (supported: int) (connection: SqliteConnection) : unit =
        let found = readVersion connection

        if found > supported then
            raise (ManifestSchemaTooNew(found, supported))

    let private stamp (connection: SqliteConnection) (transaction: SqliteTransaction) (version: int) : unit =
        execute connection transaction $"PRAGMA user_version = {version};"

    /// Applies each step from the recorded version up to `target`, one transaction apiece.
    let migrate (steps: (int * string) list) (target: int) (connection: SqliteConnection) : unit =
        for version in readVersion connection .. target - 1 do
            use transaction = connection.BeginTransaction()

            match steps |> List.tryFind (fun (from, _) -> from = version) with
            | Some(_, script) -> execute connection transaction script
            | None -> invalidOp $"There is no migration from manifest schema version {version}."

            stamp connection transaction (version + 1)
            transaction.Commit()

    /// `initialize` with the version and steps given, so that a migration can be exercised before one exists.
    let initializeWith (steps: (int * string) list) (current: int) (connection: SqliteConnection) : unit =
        // Before anything is written, including the journal mode: a newer database is left exactly as it was.
        refuseNewerThan current connection
        // WAL lets the single writer commit without locking the file, and NORMAL skips an fsync per commit while
        // never corrupting the database: only a very recent commit can be lost in a power failure.
        execute connection null "PRAGMA journal_mode = 'wal'; PRAGMA synchronous = 'normal';"

        if readVersion connection = 0 then
            use transaction = connection.BeginTransaction()

            let hasTables =
                scalar connection transaction "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'snapshots'" > 0L

            if hasTables then
                // Written before versions existed: bring it to the version 1 schema and record that.
                migrateCollationIfNeeded connection transaction
                stamp connection transaction 1
            else
                execute connection transaction createTables
                stamp connection transaction current

            transaction.Commit()

        migrate steps current connection

    /// Prepares a freshly opened writable connection: refuses a newer database, sets the journal mode, creates or
    /// stamps the tables, and applies any migration.
    let initialize (connection: SqliteConnection) : unit =
        initializeWith steps currentVersion connection
