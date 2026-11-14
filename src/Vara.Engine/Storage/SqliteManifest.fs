namespace Vara.Engine

open System
open System.Globalization
open System.IO
open Microsoft.Data.Sqlite
open Vara.Domain

/// The SQLite manifest of one profile, `.vara/profile.db`. Its schema, ISO-8601 timestamps and change-kind names are
/// part of the on-disk format, so that a target stays readable across releases. This module opens it and holds
/// what a backup run does to it; `ManifestQueries` reads history and `ManifestMaintenance` serves prune and check.
module SqliteManifest =
    open ManifestSql

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

    /// Runs `step` on a just-opened connection, which is closed again if it raises, so a refusal leaves no handle on
    /// the file.
    let private guarded (connection: SqliteConnection) (step: unit -> unit) : unit =
        try
            step ()
        with _ ->
            connection.Dispose()
            reraise ()

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
            guarded connection (fun () -> ManifestSchema.initialize connection)
            { Connection = ValueSome connection }

    /// Opens the manifest for reading only: no pragma, no table creation, no migration, no stamp, so the file is left
    /// exactly as it was (an empty handle when there is none). A reader never creates a `profile.db`. A manifest newer
    /// than this build supports is refused.
    let openReadOnly (databasePath: string) : ManifestHandle =
        if not (File.Exists databasePath) then
            { Connection = ValueNone }
        else
            let connection = connectionTo databasePath SqliteOpenMode.ReadOnly
            connection.Open()
            guarded connection (fun () -> ManifestSchema.refuseNewerThan ManifestSchema.currentVersion connection)
            { Connection = ValueSome connection }

    let close (handle: ManifestHandle) : unit =
        match handle.Connection with
        | ValueSome connection -> connection.Dispose()
        | ValueNone -> ()

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
        foldWriteAheadLog batch.BatchConnection

        { batch with
            Transaction = batch.BatchConnection.BeginTransaction() }

    /// Rolls back what was written since the last checkpoint. Earlier checkpoints stay committed.
    let discard (batch: ManifestBatch) : unit =
        batch.Transaction.Rollback()
        batch.Transaction.Dispose()

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

    /// The manifest's view of every tracked path as of the latest snapshot: the latest non-deleted version of each.
    /// Exactly one row decides per path (the one with the highest id over every casing of it, letters of every script
    /// folded), so a path recorded under several casings resolves the same way every time, and a deletion recorded
    /// under any casing removes the path. The keys compare case-insensitively and keep the latest row's casing.
    let currentState (handle: ManifestHandle) : TrackedPaths =
        let result = TrackedPaths StringComparer.OrdinalIgnoreCase

        handle
        |> readOr result (fun connection ->
            for entry in resolve connection null do
                match entry.Value.WinnerState with
                | ValueSome state -> result[entry.Key] <- state
                | ValueNone -> () // a regular row without a hash is not content the engine can place or move

            result)
