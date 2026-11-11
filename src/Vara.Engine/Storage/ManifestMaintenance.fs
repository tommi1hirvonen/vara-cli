namespace Vara.Engine

open System
open System.Globalization
open Vara.Domain

/// What prune and check need of the manifest: queries that decide nothing, and the one writer, besides a backup run,
/// that changes it.
module ManifestMaintenance =
    open ManifestSql

    /// Every content hash that some row references, link rows (which have none) excluded.
    let referencedHashes (handle: ManifestHandle) : Set<ContentHash> =
        handle
        |> readOr Set.empty (fun connection ->
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT DISTINCT content_hash FROM file_versions WHERE content_hash IS NOT NULL"
            use reader = command.ExecuteReader()

            [ while reader.Read() do
                  match ContentHash.tryCreate (reader.GetString 0) with
                  | ValueSome hash -> yield hash
                  | ValueNone -> () ]
            |> Set.ofList)

    /// Each referenced content hash with the largest size recorded for it, by hash. Link rows have no hash and are
    /// not included, so a link is never a blob to verify.
    let referencedSizes (handle: ManifestHandle) : (ContentHash * int64) list =
        handle
        |> readOr [] (fun connection ->
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT content_hash, MAX(size) FROM file_versions WHERE content_hash IS NOT NULL GROUP BY content_hash ORDER BY content_hash"

            use reader = command.ExecuteReader()

            [ while reader.Read() do
                  match ContentHash.tryCreate (reader.GetString 0) with
                  | ValueSome hash -> yield hash, reader.GetInt64 1
                  | ValueNone -> () ])

    /// The distinct paths that some row records with this content, by path.
    let pathsForHash (handle: ManifestHandle) (hash: ContentHash) : string list =
        handle
        |> readOr [] (fun connection ->
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT DISTINCT relative_path FROM file_versions WHERE content_hash = $hash ORDER BY relative_path"

            addText command "$hash" (ContentHash.value hash)
            use reader = command.ExecuteReader()
            [ while reader.Read() do yield reader.GetString 0 ])

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
