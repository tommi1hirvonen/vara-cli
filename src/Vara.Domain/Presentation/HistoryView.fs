namespace Vara.Domain

open System
open System.Globalization

/// A line of a text diff: whether the line was added, removed, or is the same on both sides.
[<RequireQualifiedAccess>]
type DiffLineKind =
    | Inserted
    | Deleted
    | Unchanged

/// What a history command shows, built as `Display` data from the values the pure history functions return. The
/// renderers draw it; nothing here knows a colour or the console.
module HistoryView =
    let private span (tone: Tone) (text: string) : Span = { Tone = tone; Text = text }
    let private plain (text: string) : Span = span Tone.Plain text

    // The date format of every listing, taken from the C# build.
    let timestamp (value: DateTimeOffset) : string =
        value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)

    let private number (value: int) : string = value.ToString CultureInfo.InvariantCulture

    /// The colour of each kind of change, the same in a file's history and in a snapshot's rollup.
    let toneOfKind (kind: ChangeKind) : Tone =
        match kind with
        | ChangeKind.Added -> Tone.Added
        | ChangeKind.Changed -> Tone.Changed
        | ChangeKind.Moved -> Tone.Moved
        | ChangeKind.Deleted -> Tone.Deleted
        | ChangeKind.Linked -> Tone.Linked

    let private kindName (kind: ChangeKind) : string =
        match kind with
        | ChangeKind.Added -> "Added"
        | ChangeKind.Changed -> "Changed"
        | ChangeKind.Moved -> "Moved"
        | ChangeKind.Deleted -> "Deleted"
        | ChangeKind.Linked -> "Linked"

    /// A snapshot's status in the severity colour of its outcome. A cancelled run is a deliberate stop and takes the
    /// warning colour, not the hard-error one.
    let toneOfStatus (status: SnapshotStatus) : Tone =
        match status with
        | SnapshotStatus.Complete -> Tone.Success
        | SnapshotStatus.Failed -> Tone.Error
        | SnapshotStatus.Cancelled -> Tone.PartialFailure
        | SnapshotStatus.Running -> Tone.Neutral

    let private statusName (status: SnapshotStatus) : string =
        match status with
        | SnapshotStatus.Running -> "Running"
        | SnapshotStatus.Complete -> "Complete"
        | SnapshotStatus.Failed -> "Failed"
        | SnapshotStatus.Cancelled -> "Cancelled"

    let private sizeOf (row: ManifestRow) : int64 =
        match row.Content with
        | RowContent.Regular content -> content.Size
        | RowContent.Linked _ -> 0L

    let private left (header: string) : Column = { Header = header; Align = Align.Left }
    let private right (header: string) : Column = { Header = header; Align = Align.Right }

    let private message (tone: Tone) (text: string) : Display = [ Line.Text(tone, text) ]

    let ofSnapshots (snapshots: SnapshotInfo list) : Display =
        match snapshots with
        | [] -> message Tone.Plain "No snapshots recorded yet."
        | _ ->
            let rows =
                snapshots
                |> List.sortByDescending _.SnapshotId
                |> List.map (fun snapshot ->
                    [ plain (snapshot.SnapshotId.ToString CultureInfo.InvariantCulture)
                      plain (timestamp snapshot.StartedAt)
                      span (toneOfStatus snapshot.Status) (statusName snapshot.Status)
                      plain (number snapshot.Stats.FilesAdded)
                      plain (number snapshot.Stats.FilesChanged)
                      plain (number snapshot.Stats.FilesMoved)
                      plain (number snapshot.Stats.FilesDeleted)
                      plain (ByteSize.format snapshot.Stats.BytesTransferred) ])

            [ Line.Table
                  { Columns =
                      [ left "#"
                        left "Started"
                        left "Status"
                        right "Added"
                        right "Changed"
                        right "Moved"
                        right "Deleted"
                        right "Transferred" ]
                    Rows = rows } ]

    /// A file's versions, newest first, each change kind in its own colour.
    let ofHistory (versions: FileVersion list) : Display =
        match versions with
        | [] -> message Tone.Plain "No version history recorded for this path."
        | _ ->
            let rows =
                versions
                |> List.map (fun version ->
                    let change =
                        match version.Row.Kind with
                        | ChangeKind.Deleted -> "Deleted (deleted)"
                        | kind -> kindName kind

                    [ plain (version.VersionId.ToString CultureInfo.InvariantCulture)
                      plain (timestamp version.Row.RecordedAt)
                      span (toneOfKind version.Row.Kind) change
                      plain (ByteSize.format (sizeOf version.Row)) ])

            [ Line.Table
                  { Columns = [ left "#"; left "Recorded"; left "Change"; right "Size" ]
                    Rows = rows } ]

    let ofListing (entries: Entry list) : Display =
        match entries with
        | [] -> message Tone.Plain "No entries recorded in this directory."
        | _ ->
            let rows =
                entries
                |> List.map (fun entry ->
                    let tone, status =
                        match entry.Status with
                        | EntryStatus.Live -> Tone.Plain, "Live"
                        | EntryStatus.Deleted -> Tone.Deleted, "Deleted"
                        | EntryStatus.Moved target -> Tone.Moved, $"Moved -> {target}"

                    let kind =
                        match entry.Kind with
                        | EntryKind.File -> "File"
                        | EntryKind.Directory -> "Directory"

                    let size =
                        match entry.Size with
                        | ValueSome bytes -> ByteSize.format bytes
                        | ValueNone -> "-"

                    [ span tone entry.Name; span tone kind; span tone status; span tone size ])

            [ Line.Table
                  { Columns = [ left "Name"; left "Kind"; left "Status"; right "Size" ]
                    Rows = rows } ]

    let ofDeleted (deleted: DeletedEntry list) : Display =
        match deleted with
        | [] -> message Tone.Plain "No deleted files recorded."
        | _ ->
            let rows =
                deleted
                |> List.map (fun entry ->
                    [ span Tone.Deleted (MirrorPath.value entry.DeletedPath)
                      span Tone.Deleted (timestamp entry.DeletedAt)
                      span Tone.Deleted (ByteSize.format entry.DeletedSize) ])

            [ Line.Table
                  { Columns = [ left "Path"; left "Deleted At"; right "Size" ]
                    Rows = rows } ]

    /// The note under a listing that fell back to the mirror root.
    let mirrorRootFallback: Display =
        message Tone.Neutral "The current directory has no recorded history; showing the mirror root instead."

    let mirrorRootFallbackForRestore: Display =
        message Tone.Neutral "The current directory has no recorded history; falling back to the mirror root."

    // The glyphs of a tree: a branch, the last branch, and the continuation under each.
    let private branch, lastBranch, trunk, blank = "├── ", "└── ", "│   ", "    "

    let normalizeScope (directory: string) : string =
        let normalized = directory.Trim().Trim([| '\\'; '/' |]).Replace('/', '\\')
        if normalized = "." then "" else normalized

    let parentOf (path: string) : string =
        match path.LastIndexOf '\\' with
        | -1 -> ""
        | index -> path.Substring(0, index)

    let nameOf (path: string) : string =
        let normalized = path.Replace('/', '\\')

        match normalized.LastIndexOf '\\' with
        | -1 -> normalized
        | index -> normalized.Substring(index + 1)

    let private depthOf (path: string) : int = path |> Seq.filter (fun c -> c = '\\') |> Seq.length

    /// How many levels below the scope root the directory is: zero for the root itself.
    let relativeDepth (scope: string) (path: string) : int =
        if String.Equals(path, scope, StringComparison.OrdinalIgnoreCase) then 0
        else
            let relative = if scope.Length = 0 then path else path.Substring(scope.Length + 1)
            depthOf relative + 1

    /// The directory at `depth` levels below the scope root that the path lies under.
    let private ancestorAtDepth (scope: string) (depth: int) (path: string) : string =
        if depth = 0 then
            scope
        else
            let relative = if scope.Length = 0 then path else path.Substring(scope.Length + 1)
            let parts = relative.Split '\\'
            let boundary = String.Join("\\", parts |> Array.truncate depth)
            if scope.Length = 0 then boundary else scope + "\\" + boundary

    /// A changed file as a tree row shows it: its name and what happened, with the old path of a move.
    let changeText (file: ChangeEntry) : string =
        let name = nameOf file.ChangedPath

        match file.ChangedKind, file.MovedFrom with
        | ChangeKind.Moved, ValueSome previous -> $"{name} - Moved from {previous}"
        | kind, _ -> $"{name} - {kindName kind}"

    let countsSpans (counts: RollupCounts) : Span list =
        [ plain " ("
          span Tone.Added $"+{counts.Added}"
          plain " "
          span Tone.Changed $"~{counts.Changed}"
          plain " "
          span Tone.Moved $"->{counts.Moved}"
          plain " "
          span Tone.Deleted $"-{counts.Deleted}"
          plain " link "
          span Tone.Linked (number counts.Linked)
          plain ")" ]

    /// The two lines that identify a snapshot and total what it changed.
    let snapshotHeader (snapshot: SnapshotInfo) : Display =
        [ Line.Text(
              Tone.Plain,
              $"Snapshot #{snapshot.SnapshotId} - {timestamp snapshot.StartedAt} - {statusName snapshot.Status}"
          )
          Line.Text(
              Tone.Plain,
              $"Changes: +{snapshot.Stats.FilesAdded} ~{snapshot.Stats.FilesChanged} ->{snapshot.Stats.FilesMoved} -{snapshot.Stats.FilesDeleted} | {ByteSize.format snapshot.Stats.BytesTransferred} transferred"
          ) ]

    /// Whether the mirror path is the mirror path of a configured source.
    let isSourceRoot (sourceRoots: Set<string>) (path: string) : bool =
        sourceRoots |> Set.exists (fun root -> String.Equals(root, normalizeScope path, StringComparison.OrdinalIgnoreCase))

    /// The directory tree of a change report, each directory with the counts of its whole subtree in the colour of each
    /// change kind. `sourceRoots` are the mirror paths of the configured sources. With a depth, only directories that
    /// many levels below the scope are shown, each boundary directory noting how many affected directories lie beneath
    /// it. Counts never change with the depth. Shown under whatever header the caller supplies.
    let treeLines
        (scopeDirectory: string)
        (sourceRoots: Set<string>)
        (depth: int voption)
        (directories: RollupDirectory list)
        : Display =
        let scope = normalizeScope scopeDirectory

        let isSource (path: string) : bool = isSourceRoot sourceRoots path

        let label (directory: RollupDirectory) (name: string) : Span list =
            plain (name + if isSource directory.DirectoryPath then " [source]" else "") :: countsSpans directory.Counts

        let visible =
            directories
            |> List.filter (fun directory ->
                match depth with
                | ValueSome limit -> relativeDepth scope directory.DirectoryPath <= limit
                | ValueNone -> true)

        let omitted =
            match depth with
            | ValueNone -> Map.empty
            | ValueSome limit ->
                directories
                |> List.filter (fun directory -> relativeDepth scope directory.DirectoryPath > limit)
                |> List.countBy (fun directory -> (ancestorAtDepth scope limit directory.DirectoryPath).ToLowerInvariant())
                |> Map.ofList

        let shown = visible |> List.map _.DirectoryPath.ToLowerInvariant() |> Set.ofList

        let childrenOf (path: string) : RollupDirectory list =
            visible
            |> List.filter (fun directory ->
                not (String.Equals(directory.DirectoryPath, scope, StringComparison.OrdinalIgnoreCase))
                && String.Equals(parentOf directory.DirectoryPath, path, StringComparison.OrdinalIgnoreCase)
                && shown.Contains(directory.DirectoryPath.ToLowerInvariant()))
            |> List.sortBy _.DirectoryPath.ToLowerInvariant()

        let fileText (file: ChangeEntry) : string = changeText file

        // The lines under a directory: its child directories, the note of omitted ones, then its files.
        let rec under (directory: RollupDirectory) (indent: string) : Line list =
            let children = childrenOf directory.DirectoryPath

            let note =
                match Map.tryFind (directory.DirectoryPath.ToLowerInvariant()) omitted with
                | Some count ->
                    let noun = if count = 1 then "directory" else "directories"
                    [ $"... {count} deeper {noun} not shown" ]
                | None -> []

            let files = directory.Files |> List.map fileText
            let total = children.Length + note.Length + files.Length

            let marker (position: int) : string = if position = total - 1 then lastBranch else branch
            let continuation (position: int) : string = if position = total - 1 then blank else trunk

            let childLines =
                children
                |> List.mapi (fun position child ->
                    Line.Spans(plain (indent + marker position) :: label child (nameOf child.DirectoryPath))
                    :: under child (indent + continuation position))
                |> List.concat

            let textLines =
                note @ files
                |> List.mapi (fun position text ->
                    Line.Spans [ plain (indent + marker (children.Length + position)); plain text ])

            childLines @ textLines

        match visible |> List.tryFind (fun directory -> String.Equals(directory.DirectoryPath, scope, StringComparison.OrdinalIgnoreCase)) with
        | Some root -> Line.Spans(label root (if scope.Length = 0 then "." else scope)) :: under root ""
        | None -> []

    /// What one snapshot changed: its identity and totals, then the directory tree.
    let ofRollup
        (snapshot: SnapshotInfo)
        (scopeDirectory: string)
        (sourceRoots: Set<string>)
        (depth: int voption)
        (directories: RollupDirectory list)
        : Display =
        snapshotHeader snapshot @ treeLines scopeDirectory sourceRoots depth directories

    /// What a restore of the plan will do, for the confirmation of a directory restore.
    let confirmation (directory: string) (plan: RestorePlan) : string =
        let removal = if plan.Removals.IsEmpty then "" else $" and remove {plan.Removals.Length} file(s)"
        $"This will write {plan.Writes.Length} file(s){removal} under '{directory}'. Continue?"

    let overwritePrompt (destination: string) : string = $"File '{destination}' already exists. Overwrite?"

    let restoredFile (path: string) (destination: string) : Display =
        message Tone.Success $"Restored '{path}' to '{destination}'."

    let restoredDirectory (directory: string) (written: int) (removed: int) (skipped: int) : Display =
        let skippedClause = if skipped > 0 then $", {skipped} link(s) skipped" else ""
        message Tone.Success $"Restored directory '{directory}': {written} file(s) written, {removed} file(s) removed{skippedClause}."

    let restoreDeclined: Display = message Tone.Neutral "Restore cancelled: destination not overwritten."

    let restoreStoppedEarly (directory: string) : Display =
        message
            Tone.PartialFailure
            $"Restore of directory '{directory}' cancelled: stopped early, some planned files may not have been written/removed."

    /// A text diff: added lines with `+`, removed with `-`, the rest as context.
    let ofDiff (lines: (DiffLineKind * string) list) : Display =
        lines
        |> List.map (fun (kind, text) ->
            match kind with
            | DiffLineKind.Inserted -> Line.Text(Tone.Added, "+ " + text)
            | DiffLineKind.Deleted -> Line.Text(Tone.Deleted, "- " + text)
            | DiffLineKind.Unchanged -> Line.Text(Tone.Plain, "  " + text))

    /// A version as the interactive picker lists it.
    let versionChoice (version: FileVersion) : Span list =
        [ plain $"#{version.VersionId} - {timestamp version.Row.RecordedAt} - "
          span (toneOfKind version.Row.Kind) (kindName version.Row.Kind)
          plain $" - {ByteSize.format (sizeOf version.Row)}" ]

    /// The picker entry that stands for the directory's current tracked state: the default.
    let currentStateChoice: Span list = [ plain "Current tracked state" ]

    /// A snapshot as the directory restore's picker lists it, scoped to the directory.
    let directoryChoice (candidate: DirectoryCandidate) : Span list =
        let net =
            if candidate.NetBytes >= 0L then "+" + ByteSize.format candidate.NetBytes else "-" + ByteSize.format -candidate.NetBytes

        let links = if candidate.TotalLinks > 0 then $", {candidate.TotalLinks} link(s)" else ""

        [ plain $"#{candidate.Snapshot.SnapshotId} - {timestamp candidate.Snapshot.StartedAt} - "
          span (toneOfStatus candidate.Snapshot.Status) (statusName candidate.Snapshot.Status)
          plain
              $" - +{candidate.FilesAdded}/~{candidate.FilesChanged}/→{candidate.FilesMoved}/-{candidate.FilesDeleted} ({net}) - {candidate.TotalFiles} file(s), {ByteSize.format candidate.TotalBytes}{links}" ]
