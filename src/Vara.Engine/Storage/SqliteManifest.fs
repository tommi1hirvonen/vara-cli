namespace Vara.Engine

open System
open System.Collections.Generic
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

    /// Where the manifest of the target rooted at `targetRoot` lives. Every caller asks here, so the layout is stated
    /// once.
    let pathIn (targetRoot: string) : string =
        Path.Combine(targetRoot, ".vara", "profile.db")

    /// A connection to the database file. The path goes through the builder, so a `;` or `=` in it cannot be read as
    /// connection-string syntax. Pooling is off: closing the connection then releases the file, with nothing to clear.
    let private connectionTo (databasePath: string) (mode: SqliteOpenMode) : SqliteConnection =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- databasePath
        builder.Mode <- mode
        builder.Pooling <- false
        new SqliteConnection(builder.ConnectionString)

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

            let connection = connectionTo databasePath SqliteOpenMode.ReadWriteCreate
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
        | ValueNone -> ()

    let private requireConnection (handle: ManifestHandle) : SqliteConnection =
        match handle.Connection with
        | ValueSome connection -> connection
        | ValueNone ->
            raise (
                InvalidOperationException
                    "This profile has no recorded backup state, so its manifest cannot be written to without first running a backup."
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

    /// Folds the write-ahead log back into the database file and empties it, so that a plain copy of the file is
    /// whole. A reader that blocks the truncation is not an error: the fold is then partial and the next one finishes.
    let private foldWriteAheadLog (connection: SqliteConnection) : unit =
        execute connection null "PRAGMA wal_checkpoint(TRUNCATE);"

    /// Commits the batch, folds the write-ahead log back into the database file so that a plain copy of it is
    /// whole between runs, and opens the transaction that continues the batch.
    let checkpoint (batch: ManifestBatch) : ManifestBatch =
        batch.Transaction.Commit()
        batch.Transaction.Dispose()
        foldWriteAheadLog batch.BatchConnection

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

    /// The row that decides a path: its id, and its state unless it is a regular row without a hash (content the
    /// engine can neither place nor move, which still decides the path and so still shadows older rows).
    [<Struct>]
    type private Winner = { WinnerId: int64; WinnerState: TrackedState voption }

    /// The winning row of every logical path: the one with the highest id over all casings of the path, compared the
    /// way the file system does (letters of every script), with the paths whose winner is a deletion left out. The SQL
    /// column folds only A-Z, so it returns the latest row of each of its own groups, deletions included, and the rows
    /// are folded here in id order into a dictionary that folds all letters. A deletion removes the key, and any other
    /// row removes it and sets it again, so the surviving key is spelled as the latest row spells it and no older row
    /// of another casing outlives a newer one. The removal comes before anything else is decided about a row.
    let private resolve (connection: SqliteConnection) (transaction: SqliteTransaction | null) : Dictionary<string, Winner> =
        let winners = Dictionary<string, Winner> StringComparer.OrdinalIgnoreCase
        use command = connection.CreateCommand()

        // Left unset when there is none: the connection then supplies the open batch's transaction, as for any read.
        if not (isNull transaction) then
            command.Transaction <- transaction

        command.CommandText <-
            """
            SELECT fv.id, fv.relative_path, fv.content_hash, fv.size, fv.source_modified_at,
                   fv.quick_hash, fv.quick_hash_scheme, fv.change_kind, fv.link_target
            FROM file_versions fv
            INNER JOIN (
                SELECT relative_path, MAX(id) AS max_id
                FROM file_versions
                GROUP BY relative_path
            ) latest ON fv.relative_path = latest.relative_path AND fv.id = latest.max_id
            ORDER BY fv.id
            """

        use reader = command.ExecuteReader()

        while reader.Read() do
            let path = reader.GetString 1
            let kind = reader.GetString 7
            winners.Remove path |> ignore

            if kind <> "Deleted" then
                let state =
                    if kind = "Linked" then
                        ValueSome(TrackedState.Linked(if reader.IsDBNull 8 then ValueNone else ValueSome(reader.GetString 8)))
                    else
                        match if reader.IsDBNull 2 then ValueNone else ContentHash.tryCreate (reader.GetString 2) with
                        | ValueSome hash ->
                            let quick =
                                if reader.IsDBNull 5 || reader.IsDBNull 6 then
                                    ValueNone
                                else
                                    ValueSome
                                        { QuickHash = reader.GetString 5
                                          QuickScheme = reader.GetInt32 6 }

                            ValueSome(
                                TrackedState.Regular
                                    { Hash = hash
                                      Size = reader.GetInt64 3
                                      SourceModifiedAt = parseIso (reader.GetString 4)
                                      Quick = quick }
                            )
                        | ValueNone -> ValueNone

                winners[path] <- { WinnerId = reader.GetInt64 0; WinnerState = state }

        winners

    /// The manifest's view of every tracked path as of the latest snapshot: the latest non-deleted version of each.
    /// Exactly one row decides per path (the one with the highest id over every casing of it, letters of every script
    /// folded), so a path recorded under several casings resolves the same way every time, and a deletion recorded
    /// under any casing removes the path. The keys compare case-insensitively and keep the latest row's casing.
    let currentState (handle: ManifestHandle) : TrackedPaths =
        let result = TrackedPaths StringComparer.OrdinalIgnoreCase

        match handle.Connection with
        | ValueNone -> result
        | ValueSome connection ->
            for entry in resolve connection null do
                match entry.Value.WinnerState with
                | ValueSome state -> result[entry.Key] <- state
                | ValueNone -> () // a regular row without a hash is not content the engine can place or move

            result

    // ---- Read-only queries for the history commands --------------------------------------------------------------
    // Each returns rows (or a reduction a database does better than the domain), and decides nothing: which version
    // was live, how a move chains and what a restore writes are the domain's. None of them writes.

    /// Opens the manifest for reading only: no pragma, no table creation, no migration, so the file is left exactly
    /// as it was (an empty handle when there is none). A reader never creates a `profile.db`.
    let openReadOnly (databasePath: string) : ManifestHandle =
        if not (File.Exists databasePath) then
            { Connection = ValueNone }
        else
            let connection = connectionTo databasePath SqliteOpenMode.ReadOnly
            connection.Open()
            { Connection = ValueSome connection }

    let private kindOf (name: string) : ChangeKind =
        match name with
        | "Added" -> ChangeKind.Added
        | "Changed" -> ChangeKind.Changed
        | "Moved" -> ChangeKind.Moved
        | "Deleted" -> ChangeKind.Deleted
        | "Linked" -> ChangeKind.Linked
        | other -> raise (InvalidDataException $"The manifest holds an unknown change kind '{other}'.")

    let private statusOf (name: string) : SnapshotStatus =
        match name with
        | "Running" -> SnapshotStatus.Running
        | "Complete" -> SnapshotStatus.Complete
        | "Failed" -> SnapshotStatus.Failed
        | "Cancelled" -> SnapshotStatus.Cancelled
        | other -> raise (InvalidDataException $"The manifest holds an unknown snapshot status '{other}'.")

    let private versionColumns =
        """
        id, snapshot_id, relative_path, previous_relative_path, content_hash, size,
        source_modified_at, change_kind, recorded_at, quick_hash, quick_hash_scheme, link_target
        """

    let private readVersion (reader: SqliteDataReader) : FileVersion =
        let text (index: int) : string voption = if reader.IsDBNull index then ValueNone else ValueSome(reader.GetString index)

        let content =
            match text 4 |> ValueOption.bind ContentHash.tryCreate with
            | ValueSome hash ->
                RowContent.Regular
                    { Hash = hash
                      Size = reader.GetInt64 5
                      SourceModifiedAt = parseIso (reader.GetString 6)
                      Quick =
                        if reader.IsDBNull 9 || reader.IsDBNull 10 then
                            ValueNone
                        else
                            ValueSome
                                { QuickHash = reader.GetString 9
                                  QuickScheme = reader.GetInt32 10 } }
            | ValueNone -> RowContent.Linked(text 11)

        { VersionId = reader.GetInt64 0
          InSnapshot = reader.GetInt64 1
          Row =
            { RowPath = MirrorPath.ofString (reader.GetString 2)
              PreviousPath = text 3 |> ValueOption.map MirrorPath.ofString
              Content = content
              Kind = kindOf (reader.GetString 7)
              RecordedAt = parseIso (reader.GetString 8) } }

    /// Runs the query and yields each row; the reader and the command live as long as the sequence is enumerated.
    let private rowsOf (handle: ManifestHandle) (sql: string) (bind: SqliteCommand -> unit) : FileVersion seq =
        match handle.Connection with
        | ValueNone -> Seq.empty
        | ValueSome connection ->
            seq {
                use command = connection.CreateCommand()
                command.CommandText <- sql
                bind command
                use reader = command.ExecuteReader()

                while reader.Read() do
                    yield readVersion reader
            }

    /// Every row of the manifest, oldest first. Lazy: a fold over it holds one row at a time.
    let allVersions (handle: ManifestHandle) : FileVersion seq =
        rowsOf handle $"SELECT {versionColumns} FROM file_versions ORDER BY id" ignore

    /// The rows recorded for one path (letter case ignored by the column's collation), newest first.
    let versionsOfPath (handle: ManifestHandle) (path: MirrorPath) : FileVersion list =
        rowsOf handle $"SELECT {versionColumns} FROM file_versions WHERE relative_path = $path ORDER BY id DESC" (fun command ->
            addText command "$path" (MirrorPath.value path))
        |> List.ofSeq

    /// The rows one snapshot recorded.
    let snapshotVersions (handle: ManifestHandle) (snapshotId: int64) : FileVersion seq =
        rowsOf handle $"SELECT {versionColumns} FROM file_versions WHERE snapshot_id = $id ORDER BY id DESC" (fun command ->
            command.Parameters.AddWithValue("$id", snapshotId) |> ignore)

    /// Whether any row is recorded for the path.
    let hasPath (handle: ManifestHandle) (path: string) : bool =
        match handle.Connection with
        | ValueNone -> false
        | ValueSome connection ->
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT 1 FROM file_versions WHERE relative_path = $path LIMIT 1"
            addText command "$path" path
            not (isNull (command.ExecuteScalar()))

    /// Whether any row is recorded under the directory prefix (which ends in a separator).
    let hasPrefix (handle: ManifestHandle) (prefix: string) : bool =
        match handle.Connection with
        | ValueNone -> false
        | ValueSome connection ->
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT 1 FROM file_versions WHERE substr(relative_path, 1, length($prefix)) = $prefix COLLATE NOCASE LIMIT 1"

            addText command "$prefix" prefix
            not (isNull (command.ExecuteScalar()))

    // ---- Queries and the writer of prune and check ---------------------------------------------------------------
    // The queries decide nothing; the writer is the one place a command other than a backup changes the manifest.

    /// Every content hash that some row references, link rows (which have none) excluded.
    let referencedHashes (handle: ManifestHandle) : Set<ContentHash> =
        match handle.Connection with
        | ValueNone -> Set.empty
        | ValueSome connection ->
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT DISTINCT content_hash FROM file_versions WHERE content_hash IS NOT NULL"
            use reader = command.ExecuteReader()

            [ while reader.Read() do
                  match ContentHash.tryCreate (reader.GetString 0) with
                  | ValueSome hash -> yield hash
                  | ValueNone -> () ]
            |> Set.ofList

    /// Each referenced content hash with the largest size recorded for it, by hash. Link rows have no hash and are
    /// not included, so a link is never a blob to verify.
    let referencedSizes (handle: ManifestHandle) : (ContentHash * int64) list =
        match handle.Connection with
        | ValueNone -> []
        | ValueSome connection ->
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT content_hash, MAX(size) FROM file_versions WHERE content_hash IS NOT NULL GROUP BY content_hash ORDER BY content_hash"

            use reader = command.ExecuteReader()

            [ while reader.Read() do
                  match ContentHash.tryCreate (reader.GetString 0) with
                  | ValueSome hash -> yield hash, reader.GetInt64 1
                  | ValueNone -> () ]

    /// The distinct paths that some row records with this content, by path.
    let pathsForHash (handle: ManifestHandle) (hash: ContentHash) : string list =
        match handle.Connection with
        | ValueNone -> []
        | ValueSome connection ->
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT DISTINCT relative_path FROM file_versions WHERE content_hash = $hash ORDER BY relative_path"

            addText command "$hash" (ContentHash.value hash)
            use reader = command.ExecuteReader()
            [ while reader.Read() do yield reader.GetString 0 ]

    /// Removes the listed snapshots in one transaction and returns how many went. A row that is the current state of
    /// its path (the highest id for the path, when that row is not a deletion) is never removed, so the live state
    /// survives the expiry of its snapshot, and a snapshot that still has such a row stays. An id that is not
    /// recorded is ignored. The highest id is taken over all rows including deletions, and over all casings of the
    /// path with the letters of every script folded, as `currentState` does: excluding deletions first would protect a
    /// stale row from before the deletion, and a row of another casing is not the path's current one.
    let removeSnapshots (handle: ManifestHandle) (snapshotIds: int64 list) : int =
        match snapshotIds with
        | [] -> 0
        | _ ->
            let connection = requireConnection handle
            // The ids are numbers, so the list is spelled into the statement as the C# build does.
            let idList = String.Join(",", snapshotIds |> List.map _.ToString(CultureInfo.InvariantCulture))

            // The caller deletes stored content next, so this commit must survive a power loss: FULL syncs the log at
            // commit, where NORMAL would leave the commit revocable. The setting cannot change inside a transaction.
            execute connection null "PRAGMA synchronous = 'full';"

            try
                let removed =
                    use transaction = connection.BeginTransaction()

                    // The keep set can hold every path ever recorded, so it goes through a table, not into the statement.
                    execute connection transaction "CREATE TEMP TABLE live_rows (id INTEGER PRIMARY KEY)"

                    use insert = connection.CreateCommand()
                    insert.Transaction <- transaction
                    insert.CommandText <- "INSERT INTO live_rows (id) VALUES ($id)"
                    let parameter = insert.CreateParameter()
                    parameter.ParameterName <- "$id"
                    insert.Parameters.Add parameter |> ignore

                    for winner in (resolve connection transaction).Values do
                        parameter.Value <- winner.WinnerId
                        insert.ExecuteNonQuery() |> ignore

                    execute
                        connection
                        transaction
                        $"DELETE FROM file_versions WHERE snapshot_id IN ({idList}) AND id NOT IN (SELECT id FROM live_rows)"

                    let removed =
                        use command = connection.CreateCommand()
                        command.Transaction <- transaction

                        command.CommandText <-
                            $"DELETE FROM snapshots WHERE id IN ({idList}) AND id NOT IN (SELECT DISTINCT snapshot_id FROM file_versions)"

                        command.ExecuteNonQuery()

                    execute connection transaction "DROP TABLE live_rows"
                    transaction.Commit()
                    removed

                foldWriteAheadLog connection
                removed
            finally
                execute connection null "PRAGMA synchronous = 'normal';"

    /// Every snapshot, newest first.
    let snapshots (handle: ManifestHandle) : SnapshotInfo list =
        match handle.Connection with
        | ValueNone -> []
        | ValueSome connection ->
            use command = connection.CreateCommand()

            command.CommandText <-
                """
                SELECT id, started_at, completed_at, status, bytes_transferred, files_added, files_changed,
                       files_moved, files_deleted, files_failed
                FROM snapshots ORDER BY id DESC
                """

            use reader = command.ExecuteReader()

            [ while reader.Read() do
                  yield
                      { SnapshotId = reader.GetInt64 0
                        StartedAt = parseIso (reader.GetString 1)
                        CompletedAt = if reader.IsDBNull 2 then ValueNone else ValueSome(parseIso (reader.GetString 2))
                        Status = statusOf (reader.GetString 3)
                        Stats =
                          { BytesTransferred = reader.GetInt64 4
                            FilesAdded = reader.GetInt32 5
                            FilesChanged = reader.GetInt32 6
                            FilesMoved = reader.GetInt32 7
                            FilesDeleted = reader.GetInt32 8
                            FilesFailed = reader.GetInt32 9 } } ]
