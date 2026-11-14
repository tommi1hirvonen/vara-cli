namespace Vara.Engine

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open Microsoft.Data.Sqlite
open Vara.Domain

/// Raised when a manifest was written by a newer build than this one: opening it could misread or damage it, so no
/// command touches it. The CLI reports it as a hard error.
type ManifestSchemaTooNew(found: int, supported: int) =
    inherit
        Exception
            $"The backup state of this profile is schema version {found}, but this version of vara supports up to version {supported}. Use a newer vara with it."

    member _.Found: int = found
    member _.Supported: int = supported

/// An open manifest. `Connection` is absent for a read-only caller whose target has no `profile.db`: every read
/// then reports no recorded state, and any write raises.
type ManifestHandle = { Connection: SqliteConnection voption }

/// The open write transaction of a run. A checkpoint commits it and returns the fresh one that replaces it.
type ManifestBatch =
    { BatchConnection: SqliteConnection
      Transaction: SqliteTransaction }

/// What the manifest's modules share: running statements, the text the manifest stores values as, and how rows are
/// read back. Internal to the manifest; its interface is `SqliteManifest`, `ManifestQueries` and
/// `ManifestMaintenance`.
module internal ManifestSql =
    let execute (connection: SqliteConnection) (transaction: SqliteTransaction | null) (sql: string) : unit =
        use command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    let addText (command: SqliteCommand) (name: string) (value: string) : unit =
        command.Parameters.AddWithValue(name, value) |> ignore

    let toIso (value: DateTimeOffset) : string = value.ToString("o", CultureInfo.InvariantCulture)

    let parseIso (value: string) : DateTimeOffset =
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

    /// The connection a write goes through. A handle without one raises: there is no state to write to.
    let requireConnection (handle: ManifestHandle) : SqliteConnection =
        match handle.Connection with
        | ValueSome connection -> connection
        | ValueNone ->
            raise (
                InvalidOperationException
                    "This profile has no recorded backup state, so its manifest cannot be written to without first running a backup."
            )

    /// Reads through the handle's connection, or gives `whenAbsent` where the handle has none: a target with no
    /// manifest has recorded nothing.
    let readOr (whenAbsent: 'T) (read: SqliteConnection -> 'T) (handle: ManifestHandle) : 'T =
        match handle.Connection with
        | ValueSome connection -> read connection
        | ValueNone -> whenAbsent

    /// Folds the write-ahead log back into the database file and empties it, so that a plain copy of the file is
    /// whole. A reader that blocks the truncation is not an error: the fold is then partial and the next one finishes.
    let foldWriteAheadLog (connection: SqliteConnection) : unit =
        execute connection null "PRAGMA wal_checkpoint(TRUNCATE);"

    // ---- The names the manifest stores -------------------------------------------------------------------------------

    let kindName (kind: ChangeKind) : string =
        match kind with
        | ChangeKind.Added -> "Added"
        | ChangeKind.Changed -> "Changed"
        | ChangeKind.Moved -> "Moved"
        | ChangeKind.Deleted -> "Deleted"
        | ChangeKind.Linked -> "Linked"

    let kindOf (name: string) : ChangeKind =
        match name with
        | "Added" -> ChangeKind.Added
        | "Changed" -> ChangeKind.Changed
        | "Moved" -> ChangeKind.Moved
        | "Deleted" -> ChangeKind.Deleted
        | "Linked" -> ChangeKind.Linked
        | other -> raise (InvalidDataException $"The manifest holds an unknown change kind '{other}'.")

    let statusName (status: SnapshotStatus) : string =
        match status with
        | SnapshotStatus.Running -> "Running"
        | SnapshotStatus.Complete -> "Complete"
        | SnapshotStatus.Failed -> "Failed"
        | SnapshotStatus.Cancelled -> "Cancelled"

    let statusOf (name: string) : SnapshotStatus =
        match name with
        | "Running" -> SnapshotStatus.Running
        | "Complete" -> SnapshotStatus.Complete
        | "Failed" -> SnapshotStatus.Failed
        | "Cancelled" -> SnapshotStatus.Cancelled
        | other -> raise (InvalidDataException $"The manifest holds an unknown snapshot status '{other}'.")

    // ---- Reading rows back -------------------------------------------------------------------------------------------

    /// The text in a nullable column.
    let optionalText (reader: SqliteDataReader) (ordinal: int) : string voption =
        if reader.IsDBNull ordinal then
            ValueNone
        else
            ValueSome(reader.GetString ordinal)

    /// The content recorded in five consecutive columns from `first`: the hash, the size, the modification time, and
    /// the quick hash with its scheme. None when the row has no usable hash, as a link's row has none.
    let readContent (reader: SqliteDataReader) (first: int) : FileContent voption =
        match optionalText reader first |> ValueOption.bind ContentHash.tryCreate with
        | ValueSome hash ->
            ValueSome
                { Hash = hash
                  Size = reader.GetInt64(first + 1)
                  SourceModifiedAt = parseIso (reader.GetString(first + 2))
                  Quick =
                    if reader.IsDBNull(first + 3) || reader.IsDBNull(first + 4) then
                        ValueNone
                    else
                        ValueSome
                            { QuickHash = reader.GetString(first + 3)
                              QuickScheme = reader.GetInt32(first + 4) } }
        | ValueNone -> ValueNone

    /// The row that decides a path: its id, and its state unless it is a regular row without a hash (content the
    /// engine can neither place nor move, which still decides the path and so still shadows older rows).
    [<Struct>]
    type Winner =
        { WinnerId: int64
          WinnerState: TrackedState voption }

    /// The winning row of every logical path: the one with the highest id over all casings of the path, compared the
    /// way the file system does (letters of every script), with the paths whose winner is a deletion left out. The SQL
    /// column folds only A-Z, so it returns the latest row of each of its own groups, deletions included, and the rows
    /// are folded here in id order into a dictionary that folds all letters. A deletion removes the key, and any other
    /// row removes it and sets it again, so the surviving key is spelled as the latest row spells it and no older row
    /// of another casing outlives a newer one. The removal comes before anything else is decided about a row.
    let resolve (connection: SqliteConnection) (transaction: SqliteTransaction | null) : Dictionary<string, Winner> =
        let winners = Dictionary<string, Winner> StringComparer.OrdinalIgnoreCase
        use command = connection.CreateCommand()

        // Left unset when there is none: the connection then supplies the open batch's transaction, as for any read.
        if not (isNull transaction) then
            command.Transaction <- transaction

        command.CommandText <-
            """
            SELECT fv.id, fv.relative_path, fv.change_kind, fv.link_target,
                   fv.content_hash, fv.size, fv.source_modified_at, fv.quick_hash, fv.quick_hash_scheme
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
            let kind = kindOf (reader.GetString 2)
            winners.Remove path |> ignore

            if kind <> ChangeKind.Deleted then
                let state =
                    if kind = ChangeKind.Linked then
                        ValueSome(TrackedState.Linked(optionalText reader 3))
                    else
                        readContent reader 4 |> ValueOption.map TrackedState.Regular

                winners[path] <- { WinnerId = reader.GetInt64 0; WinnerState = state }

        winners
