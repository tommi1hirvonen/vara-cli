namespace Vara.Engine

open Microsoft.Data.Sqlite
open Vara.Domain

/// The read-only queries of the history commands. Each returns rows (or a reduction a database does better than the
/// domain), and decides nothing: which version was live, how a move chains and what a restore writes are the domain's.
/// None of them writes, and each reads as an empty manifest from a handle that has no database.
module ManifestQueries =
    open ManifestSql

    /// The columns of a row as `readVersion` reads them. The content's five columns are consecutive, as `readContent`
    /// expects.
    let private versionColumns =
        """
        id, snapshot_id, relative_path, previous_relative_path, change_kind, recorded_at, link_target,
        content_hash, size, source_modified_at, quick_hash, quick_hash_scheme
        """

    let private readVersion (reader: SqliteDataReader) : FileVersion =
        let content =
            match readContent reader 7 with
            | ValueSome content -> RowContent.Regular content
            | ValueNone -> RowContent.Linked(optionalText reader 6)

        { VersionId = reader.GetInt64 0
          InSnapshot = reader.GetInt64 1
          Row =
            { RowPath = MirrorPath.ofString (reader.GetString 2)
              PreviousPath = optionalText reader 3 |> ValueOption.map MirrorPath.ofString
              Content = content
              Kind = kindOf (reader.GetString 4)
              RecordedAt = parseIso (reader.GetString 5) } }

    /// Runs the query and yields each row; the reader and the command live as long as the sequence is enumerated.
    let private rowsOf
        (handle: ManifestHandle)
        (sql: string)
        (bind: SqliteCommand -> unit)
        : FileVersion seq =
        handle
        |> readOr Seq.empty (fun connection ->
            seq {
                use command = connection.CreateCommand()
                command.CommandText <- sql
                bind command
                use reader = command.ExecuteReader()

                while reader.Read() do
                    yield readVersion reader
            })

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
        handle
        |> readOr false (fun connection ->
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT 1 FROM file_versions WHERE relative_path = $path LIMIT 1"
            addText command "$path" path
            not (isNull (command.ExecuteScalar())))

    /// Whether any row is recorded under the directory prefix (which ends in a separator).
    let hasPrefix (handle: ManifestHandle) (prefix: string) : bool =
        handle
        |> readOr false (fun connection ->
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT 1 FROM file_versions WHERE substr(relative_path, 1, length($prefix)) = $prefix COLLATE NOCASE LIMIT 1"

            addText command "$prefix" prefix
            not (isNull (command.ExecuteScalar())))

    /// Every snapshot, newest first.
    let snapshots (handle: ManifestHandle) : SnapshotInfo list =
        handle
        |> readOr [] (fun connection ->
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
                        CompletedAt = optionalText reader 2 |> ValueOption.map parseIso
                        Status = statusOf (reader.GetString 3)
                        Stats =
                          { BytesTransferred = reader.GetInt64 4
                            FilesAdded = reader.GetInt32 5
                            FilesChanged = reader.GetInt32 6
                            FilesMoved = reader.GetInt32 7
                            FilesDeleted = reader.GetInt32 8
                            FilesFailed = reader.GetInt32 9 } } ])
