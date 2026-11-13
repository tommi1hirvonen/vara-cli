namespace Vara.Domain

open System
open System.Collections.Generic

/// A manifest row with the identity the user sees: the version id, and the snapshot that recorded it.
/// A struct, so a fold over every row of a manifest allocates no wrapper per row.
[<Struct>]
type FileVersion =
    { VersionId: int64
      InSnapshot: int64
      Row: ManifestRow }

/// A snapshot as the manifest records it.
type SnapshotInfo =
    { SnapshotId: int64
      StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset voption
      Status: SnapshotStatus
      Stats: Counts }

/// A point in the history. A union, so a command cannot confuse a snapshot id with a time.
[<RequireQualifiedAccess>]
type Moment =
    /// The state after the given snapshot, and every snapshot before it.
    | AtSnapshot of snapshotId: int64
    /// The state of everything recorded at or before this time.
    | AtTime of DateTimeOffset
    /// The state after the latest snapshot.
    | Latest

/// How a user names one version of a file.
[<RequireQualifiedAccess>]
type VersionSelector =
    | ById of versionId: int64
    | AsOf of DateTimeOffset

[<RequireQualifiedAccess>]
type EntryKind =
    | File
    | Directory

[<RequireQualifiedAccess>]
type EntryStatus =
    | Live
    | Deleted
    /// Moved out of the listed directory; the text is the path it moved to.
    | Moved of movedTo: string

/// One line of a one-level directory listing.
type Entry =
    { Name: string
      Kind: EntryKind
      Status: EntryStatus
      Size: int64 voption }

/// A file whose latest row is a deletion.
type DeletedEntry =
    { DeletedPath: MirrorPath
      DeletedAt: DateTimeOffset
      DeletedSize: int64 }

/// What the user asked for when a tool names a version or path the history does not contain, and every other reason
/// a history command refuses. Each is an expected outcome, so it is a value; `HistoryError.render` words it.
[<RequireQualifiedAccess>]
type HistoryError =
    | NoHistory of path: string
    | IsDirectory of path: string
    | NoSuchDirectory of directory: string
    | NoVersionAt of path: string * at: DateTimeOffset
    | NoVersionWithId of path: string * versionId: int64
    | NoMatchingDirectorySnapshot of directory: string * snapshotId: int64
    | NoContentToRestore of path: string
    | NoContentToShow of path: string
    | BinaryContent of path: string
    | DiffTooLarge of path: string * limit: int64 * left: int64 voption * right: int64 voption
    | DiffBinary of path: string * left: bool * right: bool
    /// One side is over the limit and the other is binary: each side's own problem is named.
    | DiffTooLargeAndBinary of path: string * limit: int64 * left: int64 voption * right: int64 voption * leftBinary: bool * rightBinary: bool
    | DiffLinked of path: string * left: bool * right: bool
    | DestinationInMirror of destination: string
    | DestinationExists of destination: string
    | DirectoryConfirmationRequired of directory: string * writes: int * removals: int
    | UnknownSnapshot of snapshotId: int64 * profile: string

module HistoryError =
    let private sides (left: bool) (right: bool) : string =
        [ if left then "left side"
          if right then "right side" ]
        |> String.concat " and "

    let private bytes (value: int64) : string = value.ToString("N0", Globalization.CultureInfo.InvariantCulture)

    let private oversizedSides (left: int64 voption) (right: int64 voption) : string =
        [ match left with
          | ValueSome size -> $"left side is {bytes size} bytes"
          | ValueNone -> ()
          match right with
          | ValueSome size -> $"right side is {bytes size} bytes"
          | ValueNone -> () ]
        |> String.concat " and "

    let render (error: HistoryError) : string =
        match error with
        | HistoryError.NoHistory path -> $"No history exists for path '{path}'."
        | HistoryError.IsDirectory path -> $"'{path}' is a directory. Pass --recursive to restore it as a directory."
        | HistoryError.NoSuchDirectory directory -> $"No such directory is recorded in the backup: '{directory}'."
        | HistoryError.NoVersionAt(path, at) ->
            let text = at.ToString("O", Globalization.CultureInfo.InvariantCulture)
            $"No version of '{path}' existed as of {text}."
        | HistoryError.NoVersionWithId(path, versionId) -> $"No version with id {versionId} was found for path '{path}'."
        | HistoryError.NoMatchingDirectorySnapshot(directory, snapshotId) ->
            $"Snapshot {snapshotId} is not a valid candidate for directory '{directory}': it does not exist, never touched that directory, or is not Complete/Cancelled."
        | HistoryError.NoContentToRestore path ->
            $"Cannot restore '{path}': the resolved version is a symlink/junction with no stored content."
        | HistoryError.NoContentToShow path ->
            $"Cannot show '{path}': the resolved version is a symlink/junction with no stored content."
        | HistoryError.BinaryContent path ->
            $"Cannot show '{path}': content appears to be binary. Pass --force-binary to show it anyway, or redirect output to a file."
        | HistoryError.DiffTooLarge(path, limit, left, right) ->
            let described =
                [ match left with
                  | ValueSome size -> $"left side is {bytes size} bytes"
                  | ValueNone -> ()
                  match right with
                  | ValueSome size -> $"right side is {bytes size} bytes"
                  | ValueNone -> () ]
                |> String.concat " and "

            $"Cannot diff '{path}': {described}, exceeding the {bytes limit}-byte diff size limit."
        | HistoryError.DiffTooLargeAndBinary(path, limit, left, right, leftBinary, rightBinary) ->
            $"Cannot diff '{path}': {oversizedSides left right}, exceeding the {bytes limit}-byte diff size limit, and {sides leftBinary rightBinary} appears to be binary content."
        | HistoryError.DiffBinary(path, left, right) ->
            $"Cannot diff '{path}': {sides left right} appears to be binary content."
        | HistoryError.DiffLinked(path, left, right) ->
            $"Cannot diff '{path}': {sides left right} is a symlink/junction with no stored content."
        | HistoryError.DestinationInMirror destination ->
            $"Restore destination '{destination}' is inside the profile's live mirror and would corrupt it. Choose a destination outside the mirror."
        | HistoryError.DestinationExists destination ->
            $"Restore destination '{destination}' already exists. Pass --force to overwrite it."
        | HistoryError.DirectoryConfirmationRequired(directory, writes, removals) ->
            let removal = if removals > 0 then $" and remove {removals} file(s)" else ""

            $"Restoring directory '{directory}' will write {writes} file(s){removal}. Pass --force to confirm, or re-run interactively to be prompted."
        | HistoryError.UnknownSnapshot(snapshotId, profile) ->
            $"snapshot #{snapshotId} is not recorded for profile '{profile}'."

/// Directory paths as the history compares them: a directory is a prefix that ends in a separator, matched without
/// regard to letter case. The root is the empty prefix.
module Prefix =
    let private separators = [| '\\'; '/' |]

    /// "docs", "docs\", "/docs/" and "docs/" are one directory; blank and "." are the root.
    let ofDirectory (directory: string) : string =
        let trimmed = directory.Trim().Trim separators
        if trimmed.Length = 0 || trimmed = "." then "" else trimmed + "\\"

    let isRoot (directory: string) : bool = (ofDirectory directory).Length = 0

    /// What follows the prefix in the path, when the path lies under it.
    let subPath (prefix: string) (path: string) : string voption =
        if prefix.Length = 0 then ValueSome path
        elif path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then ValueSome(path.Substring prefix.Length)
        else ValueNone

    /// The name of the entry under the prefix that the path belongs to, and whether that entry is the path itself
    /// (a file) rather than a directory on the way to it.
    let immediateChild (prefix: string) (path: string) : struct (string * bool) voption =
        match subPath prefix path with
        | ValueNone -> ValueNone
        | ValueSome remainder when remainder.Length = 0 -> ValueNone
        | ValueSome remainder ->
            match remainder.IndexOfAny separators with
            | -1 -> ValueSome struct (remainder, true)
            | 0 -> ValueNone
            | index -> ValueSome struct (remainder.Substring(0, index), false)

/// What a directory restore may plan from: the path of a file at the original source location, derived from its
/// mirror path (`C\Users\x` is `C:\Users\x`).
module SourceLocation =
    /// The mirror's form of an absolute source path: the drive letter loses its colon.
    let toMirror (absolutePath: string) : string =
        if absolutePath.Length >= 2 && absolutePath[1] = ':' then
            absolutePath.Substring(0, 1) + absolutePath.Substring 2
        else
            absolutePath

    /// The absolute source path of a mirror path: a one-letter first segment is a drive.
    let fromMirror (mirrorPath: string) : string =
        let firstSegment =
            match mirrorPath.IndexOfAny [| '\\'; '/' |] with
            | -1 -> mirrorPath.Length
            | index -> index

        if firstSegment = 1 then mirrorPath.Substring(0, 1) + ":" + mirrorPath.Substring 1 else mirrorPath

/// How many entries of each change kind a directory's subtree holds.
[<Struct>]
type RollupCounts =
    { Added: int
      Changed: int
      Moved: int
      Deleted: int
      Linked: int }

/// One directory of a snapshot's change report: its own counts include every descendant, and its files are the
/// changes recorded directly in it.
type RollupDirectory =
    { DirectoryPath: string
      Counts: RollupCounts
      Files: ChangeEntry list }

/// A snapshot that touched a directory, as the directory restore offers it: what it changed there, and what the
/// directory held after it.
type DirectoryCandidate =
    { Snapshot: SnapshotInfo
      FilesAdded: int
      FilesChanged: int
      FilesMoved: int
      FilesDeleted: int
      NetBytes: int64
      TotalFiles: int
      TotalBytes: int64
      TotalLinks: int }

/// Pure decisions over the rows of a manifest: which version was live at a moment, how a file's history follows a
/// move, what a directory looked like, and what a snapshot changed. Rows are passed in as values; nothing here reads
/// a file or a clock.
module History =
    let private pathOf (version: FileVersion) : string = MirrorPath.value version.Row.RowPath

    let private isDeleted (version: FileVersion) : bool =
        match version.Row.Kind with
        | ChangeKind.Deleted -> true
        | ChangeKind.Added
        | ChangeKind.Changed
        | ChangeKind.Moved
        | ChangeKind.Linked -> false

    let private isLinked (version: FileVersion) : bool =
        match version.Row.Kind with
        | ChangeKind.Linked -> true
        | ChangeKind.Added
        | ChangeKind.Changed
        | ChangeKind.Moved
        | ChangeKind.Deleted -> false

    /// Whether a row counts at the moment. A snapshot id admits the rows of that snapshot and every earlier one.
    let private admits (moment: Moment) (version: FileVersion) : bool =
        match moment with
        | Moment.Latest -> true
        | Moment.AtSnapshot snapshotId -> version.InSnapshot <= snapshotId
        | Moment.AtTime time -> version.Row.RecordedAt <= time

    /// The newest admitted row of every path, keyed without regard to letter case. The one pass allocates the
    /// dictionary and nothing else per row.
    let private latestPerPath (moment: Moment) (versions: FileVersion seq) : Dictionary<string, FileVersion> =
        let latest = Dictionary<string, FileVersion> StringComparer.OrdinalIgnoreCase

        for version in versions do
            if admits moment version then
                let key = pathOf version

                match latest.TryGetValue key with
                | true, existing when existing.VersionId >= version.VersionId -> ()
                | _ -> latest[key] <- version

        latest

    /// The live (not deleted) version of every path at the moment: links included, deletions excluded. Order is
    /// that of the dictionary, which callers do not rely on.
    let stateAt (moment: Moment) (versions: FileVersion seq) : FileVersion[] =
        (latestPerPath moment versions).Values |> Seq.filter (isDeleted >> not) |> Array.ofSeq

    /// The paths whose latest row at the moment is a deletion.
    let deletedAt (moment: Moment) (versions: FileVersion seq) : FileVersion[] =
        (latestPerPath moment versions).Values |> Seq.filter isDeleted |> Array.ofSeq

    /// A file's history, newest first. When the earliest row recorded for the path is a move, the history goes on
    /// under the path it was moved from, and so on back; each path is visited once. `rowsOf` returns the rows
    /// recorded for one path, in any order.
    let versionsOf (rowsOf: MirrorPath -> FileVersion list) (path: MirrorPath) : FileVersion list =
        let visited = HashSet<string> StringComparer.OrdinalIgnoreCase

        let rec walk (current: MirrorPath voption) (collected: FileVersion list) : FileVersion list =
            match current with
            | ValueNone -> collected
            | ValueSome path when not (visited.Add(MirrorPath.value path)) -> collected
            | ValueSome path ->
                let rows = rowsOf path

                let next =
                    match rows with
                    | [] -> ValueNone
                    | _ ->
                        let earliest = rows |> List.minBy _.VersionId

                        match earliest.Row.Kind with
                        | ChangeKind.Moved -> earliest.Row.PreviousPath
                        | ChangeKind.Added
                        | ChangeKind.Changed
                        | ChangeKind.Deleted
                        | ChangeKind.Linked -> ValueNone

                walk next (rows @ collected)

        walk (ValueSome path) [] |> List.sortByDescending _.VersionId

    /// The version a selector names in a file's history (newest first). A deleted version is not one a user can
    /// name, and an `AsOf` time that falls on a deletion names nothing.
    let resolveVersion (path: string) (selector: VersionSelector) (history: FileVersion list) : Result<FileVersion, HistoryError> =
        match history with
        | [] -> Error(HistoryError.NoHistory path)
        | _ ->
            match selector with
            | VersionSelector.ById versionId ->
                history
                |> List.tryFind (fun version -> version.VersionId = versionId && not (isDeleted version))
                |> Result.ofOption (HistoryError.NoVersionWithId(path, versionId))
            | VersionSelector.AsOf at ->
                match history |> List.tryFind (fun version -> version.Row.RecordedAt <= at) with
                | Some version when not (isDeleted version) -> Ok version
                | Some _
                | None -> Error(HistoryError.NoVersionAt(path, at))

    /// The versions the interactive picker offers: neither a deletion nor a link has content to restore.
    let restorableVersions (history: FileVersion list) : FileVersion list =
        history |> List.filter (fun version -> not (isDeleted version) && not (isLinked version))

    /// Whether the history has a row under the directory, live or deleted, at any time.
    let hasDirectory (directory: string) (versions: FileVersion seq) : bool =
        let prefix = Prefix.ofDirectory directory
        prefix.Length = 0 || versions |> Seq.exists (fun version -> (Prefix.subPath prefix (pathOf version)).IsSome)

    /// The newest row of every path at a moment, and the newest of every path overall, from one pass over the rows.
    /// A deletion is a row like any other here, so both include the paths whose latest row deletes them.
    let reduce (moment: Moment) (versions: FileVersion seq) : struct (Dictionary<string, FileVersion> * Dictionary<string, FileVersion>) =
        let atMoment = Dictionary<string, FileVersion> StringComparer.OrdinalIgnoreCase
        let latest = Dictionary<string, FileVersion> StringComparer.OrdinalIgnoreCase

        let keep (rows: Dictionary<string, FileVersion>) (version: FileVersion) : unit =
            let key = pathOf version

            match rows.TryGetValue key with
            | true, existing when existing.VersionId >= version.VersionId -> ()
            | _ -> rows[key] <- version

        for version in versions do
            keep latest version

            if admits moment version then
                keep atMoment version

        struct (atMoment, latest)

    /// One level of a directory at the moment, entries ordered by name without regard to case.
    /// Deleted entries are included on request. A file that was moved out of the directory is marked as moved, not
    /// deleted. `versions` is every row of the manifest. Fails when nothing under the directory was ever recorded.
    let listing
        (directory: string)
        (moment: Moment)
        (includeDeleted: bool)
        (versions: FileVersion seq)
        : Result<Entry list, HistoryError> =
        let prefix = Prefix.ofDirectory directory
        let struct (atMoment, latest) = reduce moment versions

        let movedTo = Dictionary<string, string> StringComparer.OrdinalIgnoreCase

        for version in latest.Values do
            match version.Row.Kind, version.Row.PreviousPath with
            | ChangeKind.Moved, ValueSome previous -> movedTo[MirrorPath.value previous] <- pathOf version
            | _ -> ()

        let entries = Dictionary<string, Entry> StringComparer.OrdinalIgnoreCase

        // Something was recorded in the directory when any row at the moment, live or deleted, belongs under it.
        let anyTracked =
            atMoment.Values
            |> Seq.exists (fun version -> (Prefix.immediateChild prefix (pathOf version)).IsSome)

        for version in atMoment.Values do
            if not (isDeleted version) then
                match Prefix.immediateChild prefix (pathOf version) with
                | ValueNone -> ()
                | ValueSome struct (name, isLeaf) ->
                    if isLeaf then
                        entries[name] <-
                            { Name = name
                              Kind = EntryKind.File
                              Status = EntryStatus.Live
                              Size = ValueSome(
                                  match version.Row.Content with
                                  | RowContent.Regular content -> content.Size
                                  | RowContent.Linked _ -> 0L
                              ) }
                    else
                        match entries.TryGetValue name with
                        | true, existing when existing.Status = EntryStatus.Live -> ()
                        | _ ->
                            entries[name] <-
                                { Name = name
                                  Kind = EntryKind.Directory
                                  Status = EntryStatus.Live
                                  Size = ValueNone }

        for version in atMoment.Values do
            if isDeleted version then
                match Prefix.immediateChild prefix (pathOf version) with
                | ValueNone -> ()
                | ValueSome struct (name, isLeaf) ->
                    if includeDeleted && not (entries.ContainsKey name) then
                        entries[name] <-
                            if isLeaf then
                                let size =
                                    match version.Row.Content with
                                    | RowContent.Regular content -> content.Size
                                    | RowContent.Linked _ -> 0L

                                { Name = name
                                  Kind = EntryKind.File
                                  Status =
                                    match movedTo.TryGetValue(pathOf version) with
                                    | true, target -> EntryStatus.Moved target
                                    | false, _ -> EntryStatus.Deleted
                                  Size = ValueSome size }
                            else
                                { Name = name
                                  Kind = EntryKind.Directory
                                  Status = EntryStatus.Deleted
                                  Size = ValueNone }

        if anyTracked then
            entries.Values
            |> Seq.sortWith (fun left right -> String.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase))
            |> List.ofSeq
            |> Ok
        else
            Error(HistoryError.NoSuchDirectory directory)

    /// Files currently deleted under the directory, most recently deleted first, optionally only those deleted at or
    /// after `since`.
    let recentlyDeleted (directory: string) (since: DateTimeOffset voption) (versions: FileVersion seq) : DeletedEntry list =
        let prefix = Prefix.ofDirectory directory

        deletedAt Moment.Latest versions
        |> Seq.filter (fun version -> (Prefix.subPath prefix (pathOf version)).IsSome)
        |> Seq.filter (fun version ->
            match since with
            | ValueSome limit -> version.Row.RecordedAt >= limit
            | ValueNone -> true)
        |> Seq.map (fun version ->
            { DeletedPath = version.Row.RowPath
              DeletedAt = version.Row.RecordedAt
              DeletedSize =
                match version.Row.Content with
                | RowContent.Regular content -> content.Size
                | RowContent.Linked _ -> 0L })
        |> Seq.sortByDescending _.DeletedAt
        |> List.ofSeq

    let private normalizedScope (directory: string) : string =
        let normalized = directory.Trim().Trim([| '\\'; '/' |]).Replace('/', '\\')
        if normalized = "." then "" else normalized

    let private add (kind: ChangeKind) (counts: RollupCounts) : RollupCounts =
        match kind with
        | ChangeKind.Added -> { counts with Added = counts.Added + 1 }
        | ChangeKind.Changed -> { counts with Changed = counts.Changed + 1 }
        | ChangeKind.Moved -> { counts with Moved = counts.Moved + 1 }
        | ChangeKind.Deleted -> { counts with Deleted = counts.Deleted + 1 }
        | ChangeKind.Linked -> { counts with Linked = counts.Linked + 1 }

    let private noCounts: RollupCounts =
        { Added = 0
          Changed = 0
          Moved = 0
          Deleted = 0
          Linked = 0 }

    /// The directory tree of a snapshot's changes under `directory`: every directory that holds a change, directly
    /// or in a descendant, with the counts of its whole subtree. Ordered by path. With `includeFiles`, each
    /// directory also lists the changes recorded in it. Empty when the snapshot recorded nothing there.
    let rollupEntries (directory: string) (includeFiles: bool) (changes: ChangeEntry seq) : RollupDirectory list =
        let prefix = Prefix.ofDirectory directory
        let scope = prefix.TrimEnd '\\'
        let counts = Dictionary<string, RollupCounts> StringComparer.OrdinalIgnoreCase
        let files = Dictionary<string, ChangeEntry list> StringComparer.OrdinalIgnoreCase

        let parentOf (path: string) : string =
            match path.LastIndexOf '\\' with
            | -1 -> ""
            | index -> path.Substring(0, index)

        for change in changes do
            match Prefix.subPath prefix (change.ChangedPath.Replace('/', '\\')) with
            | ValueNone -> ()
            | ValueSome relative ->
                let parent = parentOf relative
                let first = if parent.Length = 0 then scope elif scope.Length = 0 then parent else scope + "\\" + parent

                if includeFiles then
                    files[first] <-
                        change :: (match files.TryGetValue first with
                                   | true, existing -> existing
                                   | false, _ -> [])

                let rec climb (current: string) : unit =
                    counts[current] <-
                        add
                            change.ChangedKind
                            (match counts.TryGetValue current with
                             | true, existing -> existing
                             | false, _ -> noCounts)

                    if not (String.Equals(current, scope, StringComparison.OrdinalIgnoreCase)) then
                        climb (
                            match current.LastIndexOf '\\' with
                            | -1 -> scope
                            | index -> current.Substring(0, index)
                        )

                climb first

        if counts.Count = 0 then
            []
        else
            counts
            |> Seq.sortWith (fun left right -> String.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase))
            |> Seq.map (fun pair ->
                { DirectoryPath = pair.Key
                  Counts = pair.Value
                  Files =
                    match files.TryGetValue pair.Key with
                    | true, list ->
                        list
                        |> List.sortWith (fun left right ->
                            String.Compare(left.ChangedPath, right.ChangedPath, StringComparison.OrdinalIgnoreCase))
                    | false, _ -> [] })
            |> List.ofSeq

    /// The entries that lie under the directory, in the order given.
    let entriesUnder (directory: string) (changes: ChangeEntry seq) : ChangeEntry[] =
        let prefix = Prefix.ofDirectory directory

        changes
        |> Seq.filter (fun change -> (Prefix.subPath prefix (change.ChangedPath.Replace('/', '\\'))).IsSome)
        |> Array.ofSeq

    /// A recorded row as a neutral change entry.
    let entryOf (version: FileVersion) : ChangeEntry =
        { ChangedPath = pathOf version
          ChangedKind = version.Row.Kind
          MovedFrom =
            match version.Row.PreviousPath with
            | ValueSome previous -> ValueSome(MirrorPath.value previous)
            | ValueNone -> ValueNone }

    /// The rollup of a snapshot's recorded rows.
    let rollup (directory: string) (includeFiles: bool) (changes: FileVersion seq) : RollupDirectory list =
        rollupEntries directory includeFiles (Seq.map entryOf changes)

    /// The snapshots of the directory restore's picker, newest first: a `Complete` or `Cancelled` snapshot that
    /// recorded at least one row under the directory, with what it changed there and what the directory held after
    /// it. A single pass over the rows in id order keeps the running state of the directory.
    let directoryCandidates (directory: string) (snapshots: SnapshotInfo list) (versions: FileVersion seq) : DirectoryCandidate list =
        let prefix = Prefix.ofDirectory directory

        let underPrefix =
            versions
            |> Seq.filter (fun version -> (Prefix.subPath prefix (pathOf version)).IsSome)
            |> Seq.sortBy _.VersionId
            |> Array.ofSeq

        let state = Dictionary<string, FileVersion> StringComparer.OrdinalIgnoreCase

        let offered (snapshot: SnapshotInfo) : bool =
            match snapshot.Status with
            | SnapshotStatus.Complete
            | SnapshotStatus.Cancelled -> true
            | SnapshotStatus.Running
            | SnapshotStatus.Failed -> false

        let byId = snapshots |> List.map (fun snapshot -> snapshot.SnapshotId, snapshot) |> Map.ofList

        underPrefix
        |> Array.groupBy _.InSnapshot
        |> Array.sortBy fst
        |> Array.choose (fun (snapshotId, rows) ->
            // What the snapshot changed here: counts of added, changed, moved and deleted rows, and the net bytes.
            let struct (added, changed, moved, deleted, net) =
                rows
                |> Array.fold
                    (fun (struct (added, changed, moved, deleted, net)) version ->
                        let size =
                            match version.Row.Content with
                            | RowContent.Regular content -> content.Size
                            | RowContent.Linked _ -> 0L

                        match version.Row.Kind with
                        | ChangeKind.Added -> struct (added + 1, changed, moved, deleted, net + size)
                        | ChangeKind.Changed -> struct (added, changed + 1, moved, deleted, net + size)
                        | ChangeKind.Moved -> struct (added, changed, moved + 1, deleted, net + size)
                        | ChangeKind.Deleted -> struct (added, changed, moved, deleted + 1, net - size)
                        | ChangeKind.Linked -> struct (added, changed, moved, deleted, net))
                    struct (0, 0, 0, 0, 0L)

            for version in rows do
                state[pathOf version] <- version

            // The state is kept for every snapshot, offered or not: a later candidate's totals include its rows.
            match Map.tryFind snapshotId byId with
            | Some snapshot when offered snapshot ->
                // What the directory holds after the snapshot: files and their bytes, and links apart.
                let struct (files, bytes, links) =
                    state.Values
                    |> Seq.filter (isDeleted >> not)
                    |> Seq.fold
                        (fun (struct (files, bytes, links)) live ->
                            match live.Row.Content with
                            | RowContent.Linked _ -> struct (files, bytes, links + 1)
                            | RowContent.Regular content -> struct (files + 1, bytes + content.Size, links))
                        struct (0, 0L, 0)

                Some
                    { Snapshot = snapshot
                      FilesAdded = added
                      FilesChanged = changed
                      FilesMoved = moved
                      FilesDeleted = deleted
                      NetBytes = net
                      TotalFiles = files
                      TotalBytes = bytes
                      TotalLinks = links }
            | Some _
            | None -> None)
        |> Array.rev
        |> List.ofArray

    /// The most a version may hold for a diff to read it into memory.
    let maxDiffBytes: int64 = 10L * 1024L * 1024L

    let private contentOf (version: FileVersion) : FileContent voption =
        match version.Row.Content with
        | RowContent.Regular content -> ValueSome content
        | RowContent.Linked _ -> ValueNone

    /// The stored content of a version; a link has none.
    let contentHash (version: FileVersion) : ContentHash voption =
        contentOf version |> ValueOption.map _.Hash

    /// The content to show for a version: a link has none.
    let showable (path: string) (version: FileVersion) : Result<ContentHash, HistoryError> =
        match contentOf version with
        | ValueSome content -> Ok content.Hash
        | ValueNone -> Error(HistoryError.NoContentToShow path)

    /// The contents of the two versions to diff, or why they cannot be: a side over the limit by its recorded size
    /// (both sides named when both are over), then a side that is a link. Decided before any content is read.
    let diffable (path: string) (left: FileVersion) (right: FileVersion) : Result<struct (ContentHash * ContentHash), HistoryError> =
        let sizeOf (version: FileVersion) : int64 =
            match contentOf version with
            | ValueSome content -> content.Size
            | ValueNone -> 0L

        let leftSize, rightSize = sizeOf left, sizeOf right
        let tooLarge (size: int64) : int64 voption = if size > maxDiffBytes then ValueSome size else ValueNone

        match tooLarge leftSize, tooLarge rightSize with
        | ValueNone, ValueNone ->
            match contentOf left, contentOf right with
            | ValueSome leftContent, ValueSome rightContent -> Ok struct (leftContent.Hash, rightContent.Hash)
            | leftContent, rightContent ->
                Error(HistoryError.DiffLinked(path, leftContent.IsNone, rightContent.IsNone))
        | oversizedLeft, oversizedRight -> Error(HistoryError.DiffTooLarge(path, maxDiffBytes, oversizedLeft, oversizedRight))

/// Whether a file's content looks binary, from a bounded sample of its first bytes: a zero byte gives it away.
module ContentSample =
    /// How many of the first bytes are examined.
    let size: int = 8000

    let looksBinary (sample: byte[]) (count: int) : bool = Array.IndexOf(sample, 0uy, 0, count) >= 0
