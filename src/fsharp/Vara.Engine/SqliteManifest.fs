namespace Vara.Engine

open System
open System.Globalization
open System.IO
open Microsoft.Data.Sqlite
open Vara.Domain

/// An open manifest. `Connection` is absent for a read-only caller whose target has no `profile.db`: every read
/// then reports no recorded state, and any write raises.
type ManifestHandle = { Connection: SqliteConnection voption }

/// The open write transaction of a run. A checkpoint commits it and returns the fresh one that replaces it.
type ManifestBatch =
    { BatchConnection: SqliteConnection
      Transaction: SqliteTransaction }

/// The SQLite manifest of one profile, `.vara/profile.db`, with the C# build's schema, ISO-8601 timestamps and
/// change-kind names, so that either build can continue a target the other wrote.
module SqliteManifest =
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

    let private execute (connection: SqliteConnection) (transaction: SqliteTransaction | null) (sql: string) : unit =
        use command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

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

    let private toIso (value: DateTimeOffset) : string = value.ToString("o", CultureInfo.InvariantCulture)

    let private parseIso (value: string) : DateTimeOffset =
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

    /// Opens, or creates, the manifest at `databasePath`. With `createIfMissing` false and no file there, nothing is
    /// created: the handle is empty, so a read-only command leaves no `profile.db` behind.
    let openAt (databasePath: string) (createIfMissing: bool) : ManifestHandle =
        if not createIfMissing && not (File.Exists databasePath) then
            { Connection = ValueNone }
        else
            match Path.GetDirectoryName databasePath with
            | null -> ()
            | directory when directory.Length = 0 -> ()
            | directory -> Directory.CreateDirectory directory |> ignore

            let connection = new SqliteConnection($"Data Source={databasePath}")
            connection.Open()
            // WAL lets the single writer commit without locking the file, and NORMAL skips an fsync per commit while
            // never corrupting the database: only a very recent commit can be lost in a power failure.
            execute connection null "PRAGMA journal_mode = 'wal'; PRAGMA synchronous = 'normal';"
            execute connection null createTables
            migrateCollationIfNeeded connection
            { Connection = ValueSome connection }

    let close (handle: ManifestHandle) : unit =
        match handle.Connection with
        | ValueSome connection ->
            connection.Dispose()
            // The provider pools connections; without this the database file stays locked after the run.
            SqliteConnection.ClearPool connection
        | ValueNone -> ()

    let private requireConnection (handle: ManifestHandle) : SqliteConnection =
        match handle.Connection with
        | ValueSome connection -> connection
        | ValueNone ->
            raise (
                InvalidOperationException(
                    "This profile has no recorded backup state, so its manifest cannot be written to without first running a backup."
                )
            )

    let private addText (command: SqliteCommand) (name: string) (value: string) : unit =
        command.Parameters.AddWithValue(name, value) |> ignore

    /// Marks every snapshot still `Running` as failed: the run that began it died.
    let reconcileIncompleteSnapshots (handle: ManifestHandle) (now: DateTimeOffset) : unit =
        use command = (requireConnection handle).CreateCommand()

        command.CommandText <-
            "UPDATE snapshots SET status = $failed, completed_at = COALESCE(completed_at, $now) WHERE status = $running"

        addText command "$failed" "Failed"
        addText command "$running" "Running"
        addText command "$now" (toIso now)
        command.ExecuteNonQuery() |> ignore

    /// Commits at once, outside any batch, so an interrupted run stays discoverable as `Running`.
    let beginSnapshot (handle: ManifestHandle) (startedAt: DateTimeOffset) : int64 =
        use command = (requireConnection handle).CreateCommand()

        command.CommandText <-
            "INSERT INTO snapshots (started_at, status) VALUES ($startedAt, $status); SELECT last_insert_rowid();"

        addText command "$startedAt" (toIso startedAt)
        addText command "$status" "Running"
        Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture)

    /// Opens the run's write transaction, so rows and the final outcome commit together at checkpoints.
    let beginBatch (handle: ManifestHandle) : ManifestBatch =
        let connection = requireConnection handle

        { BatchConnection = connection
          Transaction = connection.BeginTransaction() }

    /// Commits the batch, folds the write-ahead log back into the database file so that a plain copy of it is
    /// whole between runs, and opens the transaction that continues the batch.
    let checkpoint (batch: ManifestBatch) : ManifestBatch =
        batch.Transaction.Commit()
        batch.Transaction.Dispose()
        execute batch.BatchConnection null "PRAGMA wal_checkpoint(TRUNCATE);"

        { batch with
            Transaction = batch.BatchConnection.BeginTransaction() }

    /// Rolls back what was written since the last checkpoint. Earlier checkpoints stay committed.
    let discard (batch: ManifestBatch) : unit =
        batch.Transaction.Rollback()
        batch.Transaction.Dispose()

    let private kindName (kind: ChangeKind) : string =
        match kind with
        | ChangeKind.Added -> "Added"
        | ChangeKind.Changed -> "Changed"
        | ChangeKind.Moved -> "Moved"
        | ChangeKind.Deleted -> "Deleted"
        | ChangeKind.Linked -> "Linked"

    let private nullable (value: string voption) : obj | null =
        match value with
        | ValueSome text -> box text
        | ValueNone -> box DBNull.Value

    /// Records one row in the run's transaction. A link's row has no hash, no size and no time, only its target; a
    /// deleted link has no target.
    let record (batch: ManifestBatch) (snapshotId: int64) (row: ManifestRow) : unit =
        use command = batch.BatchConnection.CreateCommand()
        command.Transaction <- batch.Transaction

        command.CommandText <-
            """
            INSERT INTO file_versions
                (snapshot_id, relative_path, previous_relative_path, content_hash,
                 size, source_modified_at, change_kind, recorded_at, quick_hash,
                 quick_hash_scheme, link_target)
            VALUES
                ($snapshotId, $relativePath, $previousPath, $hash, $size,
                 $sourceModifiedAt, $changeKind, $recordedAt, $quickHash,
                 $quickHashScheme, $linkTarget)
            """

        let struct (hash, size, modifiedAt, quick, linkTarget) =
            match row.Content with
            | RowContent.Regular content ->
                struct (ValueSome(ContentHash.value content.Hash),
                        content.Size,
                        content.SourceModifiedAt,
                        content.Quick,
                        ValueNone)
            | RowContent.Linked target -> struct (ValueNone, 0L, DateTimeOffset.MinValue, ValueNone, target)

        let parameters = command.Parameters
        parameters.AddWithValue("$snapshotId", snapshotId) |> ignore
        parameters.AddWithValue("$relativePath", MirrorPath.value row.RowPath) |> ignore

        parameters.AddWithValue("$previousPath", nullable (row.PreviousPath |> ValueOption.map MirrorPath.value))
        |> ignore

        parameters.AddWithValue("$hash", nullable hash) |> ignore
        parameters.AddWithValue("$size", size) |> ignore
        parameters.AddWithValue("$sourceModifiedAt", toIso modifiedAt) |> ignore
        parameters.AddWithValue("$changeKind", kindName row.Kind) |> ignore
        parameters.AddWithValue("$recordedAt", toIso row.RecordedAt) |> ignore

        match quick with
        | ValueSome signature ->
            parameters.AddWithValue("$quickHash", signature.QuickHash) |> ignore
            parameters.AddWithValue("$quickHashScheme", signature.QuickScheme) |> ignore
        | ValueNone ->
            parameters.AddWithValue("$quickHash", DBNull.Value) |> ignore
            parameters.AddWithValue("$quickHashScheme", DBNull.Value) |> ignore

        parameters.AddWithValue("$linkTarget", nullable linkTarget) |> ignore
        command.ExecuteNonQuery() |> ignore

    let private statusName (status: SnapshotStatus) : string =
        match status with
        | SnapshotStatus.Running -> "Running"
        | SnapshotStatus.Complete -> "Complete"
        | SnapshotStatus.Failed -> "Failed"
        | SnapshotStatus.Cancelled -> "Cancelled"

    /// Writes how the snapshot ended, with its totals, inside the run's transaction.
    let finishSnapshot
        (batch: ManifestBatch)
        (snapshotId: int64)
        (status: SnapshotStatus)
        (finishedAt: DateTimeOffset)
        (counts: Counts)
        : unit =
        use command = batch.BatchConnection.CreateCommand()
        command.Transaction <- batch.Transaction

        command.CommandText <-
            """
            UPDATE snapshots
            SET completed_at = $completedAt, status = $status,
                bytes_transferred = $bytes, files_added = $added, files_changed = $changed,
                files_moved = $moved, files_deleted = $deleted, files_failed = $failed
            WHERE id = $id
            """

        let parameters = command.Parameters
        parameters.AddWithValue("$completedAt", toIso finishedAt) |> ignore
        parameters.AddWithValue("$status", statusName status) |> ignore
        parameters.AddWithValue("$bytes", counts.BytesTransferred) |> ignore
        parameters.AddWithValue("$added", counts.FilesAdded) |> ignore
        parameters.AddWithValue("$changed", counts.FilesChanged) |> ignore
        parameters.AddWithValue("$moved", counts.FilesMoved) |> ignore
        parameters.AddWithValue("$deleted", counts.FilesDeleted) |> ignore
        parameters.AddWithValue("$failed", counts.FilesFailed) |> ignore
        parameters.AddWithValue("$id", snapshotId) |> ignore
        command.ExecuteNonQuery() |> ignore

    /// The latest non-deleted version of every path, optionally only versions recorded at or before `asOf`.
    /// Exactly one row decides per path (the one with the highest id), so a path recorded under several casings
    /// resolves the same way every time. The keys compare case-insensitively and keep the latest row's casing.
    let private readCurrentState (handle: ManifestHandle) (asOf: DateTimeOffset voption) : TrackedPaths =
        let result = TrackedPaths(StringComparer.OrdinalIgnoreCase)

        match handle.Connection with
        | ValueNone -> result
        | ValueSome connection ->
            use command = connection.CreateCommand()
            let restriction = if asOf.IsSome then "WHERE recorded_at <= $asOf" else ""

            command.CommandText <-
                $"""
                SELECT fv.relative_path, fv.content_hash, fv.size, fv.source_modified_at,
                       fv.quick_hash, fv.quick_hash_scheme, fv.change_kind, fv.link_target
                FROM file_versions fv
                INNER JOIN (
                    SELECT relative_path, MAX(id) AS max_id
                    FROM file_versions
                    {restriction}
                    GROUP BY relative_path
                ) latest ON fv.relative_path = latest.relative_path AND fv.id = latest.max_id
                WHERE fv.change_kind != $deleted
                """

            match asOf with
            | ValueSome cutoff -> addText command "$asOf" (toIso (cutoff.ToUniversalTime()))
            | ValueNone -> ()

            addText command "$deleted" "Deleted"
            use reader = command.ExecuteReader()

            while reader.Read() do
                let path = reader.GetString 0

                if reader.GetString 6 = "Linked" then
                    result[path] <- TrackedState.Linked(if reader.IsDBNull 7 then ValueNone else ValueSome(reader.GetString 7))
                else
                    match (if reader.IsDBNull 1 then ValueNone else ContentHash.tryCreate (reader.GetString 1)) with
                    | ValueSome hash ->
                        let quick =
                            if reader.IsDBNull 4 || reader.IsDBNull 5 then
                                ValueNone
                            else
                                ValueSome
                                    { QuickHash = reader.GetString 4
                                      QuickScheme = reader.GetInt32 5 }

                        result[path] <-
                            TrackedState.Regular
                                { Hash = hash
                                  Size = reader.GetInt64 2
                                  SourceModifiedAt = parseIso (reader.GetString 3)
                                  Quick = quick }
                    | ValueNone -> () // a regular row without a hash is not content the engine can place or move

            result

    /// The manifest's view of every tracked path as of the latest snapshot.
    let currentState (handle: ManifestHandle) : TrackedPaths = readCurrentState handle ValueNone
