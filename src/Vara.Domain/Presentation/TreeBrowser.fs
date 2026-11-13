namespace Vara.Domain

open System
open System.Collections.Generic

/// A key the tree browser understands. Anything else a terminal reports is `Other` and ignored.
[<RequireQualifiedAccess>]
type Key =
    | Up
    | Down
    | Left
    | Right
    | Home
    | End
    | ExpandAll
    /// Shows or hides the file rows of deleted files.
    | ToggleDeleted
    /// Shows or hides all file rows.
    | ToggleFiles
    | Quit
    | Other

/// One directory of the tree: where it sits, the counts of its whole subtree, and the slice of the sorted file array that
/// holds the files recorded directly in it. `Absorbed` counts the deleted entries in its subtree that no row shows because
/// a move absorbs them.
[<Struct>]
type TreeNode =
    { Path: string
      Parent: int
      Children: int[]
      Counts: RollupCounts
      Depth: int
      FileStart: int
      FileCount: int
      Absorbed: int }

/// What the browser reads once and never changes: the directories as an array with parent and child indexes, and the
/// changed files as one array ordered by directory and then name, so that a directory's files are a slice of it. A class,
/// so that a state compares it by reference.
[<Sealed; AllowNullLiteral>]
type TreeIndex(nodes: TreeNode[], files: ChangeEntry[], root: int, deletedFiles: int) =
    member _.Nodes = nodes
    member _.Files = files
    /// The node of the scope root, or -1 when the scope holds no change.
    member _.Root = root
    /// How many of the files are deleted entries that are listed.
    member _.DeletedFiles = deletedFiles

/// A visible row: a directory (`File` is -1) or a changed file of a directory. `Parent` is the row of the directory above it.
[<Struct>]
type TreeRow =
    { Node: int
      File: int
      Parent: int
      Level: int
      IsLast: bool }

/// The state of one browsing session. The header is the lines above the tree; the offset is the first visible row; the
/// rows are the visible rows, kept until the expansion or a toggle changes them, and the cursor is an index into them.
type TreeState =
    { Header: Display
      Scope: string
      SourceRoots: Set<string>
      Index: TreeIndex
      Expanded: Set<int>
      ShowDeleted: bool
      ShowFiles: bool
      Rows: TreeRow[]
      Cursor: int
      Offset: int
      Finished: bool }

/// A change tree that is navigated with keys. Pure: a state and a key give the next state, and a state and a height
/// give the lines to draw, so a session is tested without a terminal.
module TreeBrowser =
    let private span (tone: Tone) (text: string) : Span = { Tone = tone; Text = text }
    let private plain (text: string) : Span = span Tone.Plain text

    // The guides of a tree, as in the static report, so a console without Unicode maps them to ASCII the same way.
    let private branch, lastBranch, trunk, blank = "├── ", "└── ", "│   ", "    "

    // State markers and the cursor are ASCII, so they need no mapping.
    let private collapsedMarker, expandedMarker, leafMarker = "[+] ", "[-] ", "    "
    let private cursorGutter, noGutter = "> ", "  "

    let private symbolOf (kind: ChangeKind) : string =
        match kind with
        | ChangeKind.Added -> "+ "
        | ChangeKind.Changed -> "~ "
        | ChangeKind.Moved -> "->"
        | ChangeKind.Deleted -> "- "
        | ChangeKind.Linked -> "@ "

    let private normalize (path: string) : string = if path.Contains '/' then path.Replace('/', '\\') else path

    /// The names of two files compared without case, from the last backslash of each path, without copying either.
    let private compareNames (left: string) (right: string) : int =
        String.Compare(left, left.LastIndexOf '\\' + 1, right, right.LastIndexOf '\\' + 1, Int32.MaxValue, StringComparison.OrdinalIgnoreCase)

    /// Builds the index of a scope's directories and changed files. A deleted entry that is the recorded previous path of
    /// a moved entry is not listed as a file; the move's own row stands for it.
    let private buildIndex (scope: string) (directories: RollupDirectory list) (entries: ChangeEntry[]) : TreeIndex =
        let dirs = Array.ofList directories
        let lookup = Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        dirs |> Array.iteri (fun index directory -> lookup[directory.DirectoryPath] <- index)

        let isScopePath (path: string) = String.Equals(path, scope, StringComparison.OrdinalIgnoreCase)

        let parents =
            dirs
            |> Array.map (fun directory ->
                if isScopePath directory.DirectoryPath then
                    -1
                else
                    match lookup.TryGetValue(HistoryView.parentOf directory.DirectoryPath) with
                    | true, parent -> parent
                    | false, _ -> -1)

        let kids = Array.init dirs.Length (fun _ -> ResizeArray<int>())

        parents |> Array.iteri (fun index parent -> if parent >= 0 then kids[parent].Add index)

        for children in kids do
            children.Sort(fun a b -> String.CompareOrdinal(dirs[a].DirectoryPath.ToLowerInvariant(), dirs[b].DirectoryPath.ToLowerInvariant()))

        let movedFrom = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        for entry in entries do
            match entry.ChangedKind, entry.MovedFrom with
            | ChangeKind.Moved, ValueSome previous -> movedFrom.Add(normalize previous) |> ignore
            | _ -> ()

        let absorbed = Array.zeroCreate<int> dirs.Length
        let kept = ResizeArray<struct (int * ChangeEntry)>()

        for entry in entries do
            let path = normalize entry.ChangedPath

            match lookup.TryGetValue(HistoryView.parentOf path) with
            | true, node ->
                if entry.ChangedKind = ChangeKind.Deleted && movedFrom.Contains path then
                    let mutable current = node

                    while current >= 0 do
                        absorbed[current] <- absorbed[current] + 1
                        current <- parents[current]
                else
                    kept.Add(struct (node, entry))
            | false, _ -> ()

        let ordered =
            kept.ToArray()
            |> Array.sortWith (fun (struct (leftNode, left)) (struct (rightNode, right)) ->
                if leftNode <> rightNode then compare leftNode rightNode else compareNames left.ChangedPath right.ChangedPath)

        let files = ordered |> Array.map (fun (struct (_, entry)) -> entry)
        let starts = Array.create dirs.Length 0
        let counts = Array.zeroCreate<int> dirs.Length

        ordered
        |> Array.iteri (fun position (struct (node, _)) ->
            if counts[node] = 0 then starts[node] <- position
            counts[node] <- counts[node] + 1)

        let nodes =
            dirs
            |> Array.mapi (fun index directory ->
                { Path = directory.DirectoryPath
                  Parent = parents[index]
                  Children = kids[index].ToArray()
                  Counts = directory.Counts
                  Depth = HistoryView.relativeDepth scope directory.DirectoryPath
                  FileStart = starts[index]
                  FileCount = counts[index]
                  Absorbed = absorbed[index] })

        let root = dirs |> Array.tryFindIndex (fun directory -> isScopePath directory.DirectoryPath) |> Option.defaultValue -1
        TreeIndex(nodes, files, root, files |> Array.sumBy (fun entry -> if entry.ChangedKind = ChangeKind.Deleted then 1 else 0))

    let private isListed (showDeleted: bool) (showFiles: bool) (entry: ChangeEntry) : bool =
        showFiles && (showDeleted || entry.ChangedKind <> ChangeKind.Deleted)

    let private visibleFileCount (index: TreeIndex) (showDeleted: bool) (showFiles: bool) (node: TreeNode) : int =
        if not showFiles then
            0
        elif showDeleted then
            node.FileCount
        else
            let mutable count = 0

            for position in node.FileStart .. node.FileStart + node.FileCount - 1 do
                if index.Files[position].ChangedKind <> ChangeKind.Deleted then count <- count + 1

            count

    /// Whether a directory has anything to show beneath it: a child directory or a listed file.
    let private isExpandable (index: TreeIndex) (showDeleted: bool) (showFiles: bool) (node: TreeNode) : bool =
        node.Children.Length > 0 || visibleFileCount index showDeleted showFiles node > 0

    /// The visible rows, from the scope root down: an expanded directory's child directories, then its listed files.
    let private buildRows (index: TreeIndex) (expanded: Set<int>) (showDeleted: bool) (showFiles: bool) : TreeRow[] =
        let rows = ResizeArray<TreeRow>()

        let rec add (nodeIndex: int) (level: int) (isLast: bool) (parent: int) : unit =
            let node = index.Nodes[nodeIndex]
            let row = rows.Count

            rows.Add
                { Node = nodeIndex
                  File = -1
                  Parent = parent
                  Level = level
                  IsLast = isLast }

            if expanded.Contains nodeIndex then
                let listed = visibleFileCount index showDeleted showFiles node
                let total = node.Children.Length + listed

                node.Children |> Array.iteri (fun position child -> add child (level + 1) (position = total - 1) row)

                let mutable position = node.Children.Length

                for file in node.FileStart .. node.FileStart + node.FileCount - 1 do
                    if isListed showDeleted showFiles index.Files[file] then
                        rows.Add
                            { Node = nodeIndex
                              File = file
                              Parent = row
                              Level = level + 1
                              IsLast = position = total - 1 }

                        position <- position + 1

        if index.Root >= 0 then add index.Root 0 true -1

        rows.ToArray()

    /// A session over a scope's directories and changed files. The scope root and every directory fewer levels below it
    /// than the depth (one level when there is none) start expanded; the cursor is on the scope root. `directories` carry
    /// the counts, `entries` the files, every change under the scope.
    let create
        (header: Display)
        (scopeDirectory: string)
        (sourceRoots: Set<string>)
        (depth: int voption)
        (directories: RollupDirectory list)
        (entries: ChangeEntry[])
        : TreeState =
        let scope = HistoryView.normalizeScope scopeDirectory
        let limit = match depth with ValueSome value -> value | ValueNone -> 1
        let index = buildIndex scope directories entries

        let expanded =
            index.Nodes
            |> Array.mapi (fun position node -> struct (position, node))
            |> Array.filter (fun (struct (_, node)) -> node.Depth < limit && isExpandable index true true node)
            |> Array.map (fun (struct (position, _)) -> position)
            |> Set.ofArray

        { Header = header
          Scope = scope
          SourceRoots = sourceRoots
          Index = index
          Expanded = expanded
          ShowDeleted = true
          ShowFiles = true
          Rows = buildRows index expanded true true
          Cursor = 0
          Offset = 0
          Finished = false }

    /// The state with its rows rebuilt from the expansion and the toggles. The cursor stays on its row, or on the nearest
    /// visible ancestor of it when that row is gone.
    let private rebuild (state: TreeState) : TreeState =
        let rows = buildRows state.Index state.Expanded state.ShowDeleted state.ShowFiles

        let cursor =
            if state.Rows.Length = 0 || rows.Length = 0 then
                0
            else
                let before = state.Rows[state.Cursor]
                let find (node: int) (file: int) = rows |> Array.tryFindIndex (fun row -> row.Node = node && row.File = file)

                let rec climb (node: int) : int =
                    match find node -1 with
                    | Some position -> position
                    | None when node > 0 && state.Index.Nodes[node].Parent >= 0 -> climb state.Index.Nodes[node].Parent
                    | None -> 0

                match find before.Node before.File with
                | Some position -> position
                | None -> climb before.Node

        { state with Rows = rows; Cursor = cursor }

    let private moveTo (position: int) (state: TreeState) : TreeState =
        if position < 0 || position >= state.Rows.Length then state else { state with Cursor = position }

    /// The state after a key.
    let step (pressed: Key) (state: TreeState) : TreeState =
        let rows = state.Rows
        let cursor = state.Cursor
        let index = state.Index

        match pressed with
        | Key.Quit -> { state with Finished = true }
        | Key.Other -> state
        | _ when rows.Length = 0 -> state
        | Key.Up -> moveTo (max 0 (cursor - 1)) state
        | Key.Down -> moveTo (min (rows.Length - 1) (cursor + 1)) state
        | Key.Home -> moveTo 0 state
        | Key.End -> moveTo (rows.Length - 1) state
        | Key.ToggleDeleted -> rebuild { state with ShowDeleted = not state.ShowDeleted }
        | Key.ToggleFiles -> rebuild { state with ShowFiles = not state.ShowFiles }
        | Key.ExpandAll ->
            let all =
                index.Nodes
                |> Array.mapi (fun position node -> struct (position, node))
                |> Array.filter (fun (struct (_, node)) -> isExpandable index state.ShowDeleted state.ShowFiles node)
                |> Array.map (fun (struct (position, _)) -> position)
                |> Set.ofArray

            rebuild { state with Expanded = all }
        | Key.Right ->
            let row = rows[cursor]

            if row.File >= 0 then
                state
            else
                let node = index.Nodes[row.Node]

                if not (isExpandable index state.ShowDeleted state.ShowFiles node) then state
                elif state.Expanded.Contains row.Node then
                    if cursor + 1 < rows.Length && rows[cursor + 1].Parent = cursor then moveTo (cursor + 1) state else state
                else
                    rebuild { state with Expanded = state.Expanded.Add row.Node }
        | Key.Left ->
            let row = rows[cursor]

            if row.File < 0
               && state.Expanded.Contains row.Node
               && isExpandable index state.ShowDeleted state.ShowFiles index.Nodes[row.Node] then
                rebuild { state with Expanded = state.Expanded.Remove row.Node }
            elif row.Parent >= 0 then
                moveTo row.Parent state
            else
                state

    let private hints = "Up/Down move  Right/Left expand/collapse  Home/End  * expand all  q quit"

    /// A line cut to the width, with an ellipsis at the cut: from the end, or from the start for a path, whose last
    /// segments are the ones that tell.
    let private fit (width: int) (keepEnd: bool) (text: string) : string =
        if width < 4 || text.Length <= width then text
        elif keepEnd then "..." + text.Substring(text.Length - (width - 3))
        else text.Substring(0, width - 3) + "..."

    /// The guide characters in front of a row: one column per ancestor below the scope root, then the row's own branch.
    let private guideOf (rows: TreeRow[]) (position: int) : string =
        let row = rows[position]

        if row.Level = 0 then
            ""
        else
            let columns = Stack<string>()
            let mutable ancestor = row.Parent

            while ancestor >= 0 && rows[ancestor].Level >= 1 do
                columns.Push(if rows[ancestor].IsLast then blank else trunk)
                ancestor <- rows[ancestor].Parent

            String.Concat(columns) + (if row.IsLast then lastBranch else branch)

    let private rowLine (state: TreeState) (position: int) (isCursor: bool) : Line =
        let rows = state.Rows
        let row = rows[position]
        let index = state.Index
        let gutter = if isCursor then cursorGutter else noGutter
        let prefix = gutter + guideOf rows position

        if row.File >= 0 then
            let entry = index.Files[row.File]
            let tone = HistoryView.toneOfKind entry.ChangedKind

            Line.Spans
                [ plain prefix
                  span tone (symbolOf entry.ChangedKind)
                  plain (" " + HistoryView.nameOf entry.ChangedPath) ]
        else
            let node = index.Nodes[row.Node]

            let marker =
                if not (isExpandable index state.ShowDeleted state.ShowFiles node) then leafMarker
                elif state.Expanded.Contains row.Node then expandedMarker
                else collapsedMarker

            let name =
                if row.Level = 0 then (if state.Scope.Length = 0 then "." else state.Scope)
                else HistoryView.nameOf node.Path

            let source = if HistoryView.isSourceRoot state.SourceRoots node.Path then " [source]" else ""
            Line.Spans(plain (prefix + marker) :: plain (name + source) :: HistoryView.countsSpans node.Counts)

    /// What is said about the row under the cursor: its full path and, for a moved file, where it came from; for a
    /// directory, how many deleted entries moves absorb beneath it.
    let private detailOf (state: TreeState) (row: TreeRow) : string =
        let index = state.Index

        if row.File >= 0 then
            let entry = index.Files[row.File]

            match entry.ChangedKind, entry.MovedFrom with
            | ChangeKind.Moved, ValueSome previous -> $"{entry.ChangedPath}  (moved from {previous})"
            | _ -> entry.ChangedPath
        else
            let node = index.Nodes[row.Node]
            let path = if row.Level = 0 && state.Scope.Length = 0 then "(mirror root)" else node.Path

            if node.Absorbed > 0 then
                let noun = if node.Absorbed = 1 then "entry" else "entries"
                $"{path}  ({node.Absorbed} deleted {noun} not listed: moved)"
            else
                path

    let private toggleStatus (state: TreeState) : string list =
        let files = state.Index.Files.Length

        let deleted =
            if state.ShowDeleted then "d deleted: shown" else $"d deleted: hidden ({state.Index.DeletedFiles})"

        let shown = if state.ShowFiles then "f files: shown" else $"f files: hidden ({files})"
        [ deleted; shown ]

    /// The lines of one frame for a terminal of the given width and height, and the state with its scroll offset fitted so
    /// the cursor row is shown and no empty space is left while rows are hidden above. The tree gets the height less the
    /// header, the detail line and the status line, and at least one row. The detail and status lines are cut to the
    /// width, the status line first giving up its key hints.
    let frame (width: int) (height: int) (state: TreeState) : TreeState * Display =
        let rows = state.Rows
        let cursor = if rows.Length = 0 then 0 else min state.Cursor (rows.Length - 1)
        let capacity = max 1 (height - state.Header.Length - 2)
        let tooFar = if cursor < state.Offset then cursor else state.Offset
        let fitted = if cursor >= tooFar + capacity then cursor - capacity + 1 else tooFar
        let offset = max 0 (min fitted (max 0 (rows.Length - capacity)))
        let last = min rows.Length (offset + capacity)

        let lines = [ for position in offset .. last - 1 -> rowLine state position (position = cursor) ]

        let detail =
            if rows.Length = 0 then Line.Text(Tone.Detail, "")
            else Line.Text(Tone.Detail, fit (width - 1) true (detailOf state rows[cursor]))

        let more =
            [ if offset > 0 then $"({offset} more above)"
              if rows.Length - last > 0 then $"({rows.Length - last} more below)" ]

        let withHints = String.Join("  ", more @ toggleStatus state @ [ hints ])
        let withoutHints = String.Join("  ", more @ toggleStatus state @ [ "q quit" ])

        let status =
            if withHints.Length <= width - 1 then withHints
            else fit (width - 1) false withoutHints

        { state with Cursor = cursor; Offset = offset }, state.Header @ lines @ [ detail; Line.Text(Tone.Neutral, status) ]
