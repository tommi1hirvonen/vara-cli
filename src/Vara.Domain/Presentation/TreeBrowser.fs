namespace Vara.Domain

open System

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
    | Quit
    | Other

/// The identity of a row: a directory by its path, or a file by its directory and its place among that directory's
/// files. The cursor is an identity, so it stays on the same logical row while rows above it appear and disappear.
[<RequireQualifiedAccess>]
type RowId =
    | Dir of path: string
    | File of directory: string * index: int

/// The state of one browsing session. The header is the lines above the tree; the offset is the first visible row.
type TreeState =
    { Header: Display
      Scope: string
      SourceRoots: Set<string>
      Directories: RollupDirectory list
      Expanded: Set<string>
      Cursor: RowId
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

    let private key (path: string) : string = path.ToLowerInvariant()

    /// A row as it is shown, with the identity navigation works on.
    type private VisibleRow =
        { Id: RowId
          Parent: RowId voption
          Indent: string
          Marker: string
          Label: Span list }

    let private isScope (state: TreeState) (path: string) : bool =
        String.Equals(path, state.Scope, StringComparison.OrdinalIgnoreCase)

    let private childrenOf (state: TreeState) (directory: RollupDirectory) : RollupDirectory list =
        state.Directories
        |> List.filter (fun candidate ->
            not (isScope state candidate.DirectoryPath)
            && String.Equals(HistoryView.parentOf candidate.DirectoryPath, directory.DirectoryPath, StringComparison.OrdinalIgnoreCase))
        |> List.sortBy _.DirectoryPath.ToLowerInvariant()

    let private isExpandable (state: TreeState) (directory: RollupDirectory) : bool =
        not directory.Files.IsEmpty || not (childrenOf state directory).IsEmpty

    let private label (state: TreeState) (directory: RollupDirectory) (name: string) : Span list =
        let source = if HistoryView.isSourceRoot state.SourceRoots directory.DirectoryPath then " [source]" else ""
        plain (name + source) :: HistoryView.countsSpans directory.Counts

    let private root (state: TreeState) : RollupDirectory option =
        state.Directories |> List.tryFind (fun directory -> isScope state directory.DirectoryPath)

    /// The rows the expanded directories show, from the scope root down, in the order they are drawn.
    let private visibleRows (state: TreeState) : VisibleRow list =
        let rows = ResizeArray<VisibleRow>()

        let rec add (directory: RollupDirectory) (indent: string) (guide: string) (name: string) (parent: RowId voption) : unit =
            let expandable = isExpandable state directory
            let expanded = expandable && state.Expanded.Contains(key directory.DirectoryPath)

            rows.Add
                { Id = RowId.Dir directory.DirectoryPath
                  Parent = parent
                  Indent = indent + guide
                  Marker = if not expandable then leafMarker elif expanded then expandedMarker else collapsedMarker
                  Label = label state directory name }

            if expanded then
                let children = childrenOf state directory
                let total = children.Length + directory.Files.Length

                let isLast (position: int) = position = total - 1
                // The children of the root hang from the root's own column; the root draws no guide itself.
                let below = if guide.Length = 0 then indent else indent + (if guide = lastBranch then blank else trunk)

                children
                |> List.iteri (fun position child ->
                    add child below (if isLast position then lastBranch else branch) (HistoryView.nameOf child.DirectoryPath) (ValueSome(RowId.Dir directory.DirectoryPath)))

                directory.Files
                |> List.iteri (fun position file ->
                    rows.Add
                        { Id = RowId.File(directory.DirectoryPath, position)
                          Parent = ValueSome(RowId.Dir directory.DirectoryPath)
                          Indent = below + (if isLast (children.Length + position) then lastBranch else branch)
                          Marker = leafMarker
                          Label = [ plain (HistoryView.changeText file) ] })

        match root state with
        | Some directory -> add directory "" "" (if state.Scope.Length = 0 then "." else state.Scope) ValueNone
        | None -> ()

        List.ofSeq rows

    /// The effective depth of the first view: the requested depth, or one level when none was requested.
    let private effectiveDepth (depth: int voption) : int =
        match depth with
        | ValueSome limit -> limit
        | ValueNone -> 1

    /// A session over a snapshot's directories. The scope root and every directory fewer levels below it than the depth
    /// start expanded; the cursor is on the scope root.
    let create
        (header: Display)
        (scopeDirectory: string)
        (sourceRoots: Set<string>)
        (depth: int voption)
        (directories: RollupDirectory list)
        : TreeState =
        let scope = HistoryView.normalizeScope scopeDirectory
        let limit = effectiveDepth depth

        let initial =
            { Header = header
              Scope = scope
              SourceRoots = sourceRoots
              Directories = directories
              Expanded = Set.empty
              Cursor = RowId.Dir scope
              Offset = 0
              Finished = false }

        let expanded =
            directories
            |> List.filter (fun directory -> HistoryView.relativeDepth scope directory.DirectoryPath < limit && isExpandable initial directory)
            |> List.map (fun directory -> key directory.DirectoryPath)
            |> Set.ofList

        { initial with Expanded = expanded }

    /// The identity of the row the cursor is really on: the cursor itself when its row is shown, otherwise the nearest
    /// shown ancestor, so a collapse never leaves the cursor on a row that is gone.
    let private settle (state: TreeState) (rows: VisibleRow list) : RowId =
        let shown (id: RowId) = rows |> List.exists (fun row -> row.Id = id)

        let rec climb (path: string) : RowId =
            if shown (RowId.Dir path) || path.Length = 0 || isScope state path then
                RowId.Dir(if shown (RowId.Dir path) then path else state.Scope)
            else
                climb (HistoryView.parentOf path)

        if shown state.Cursor then
            state.Cursor
        else
            match state.Cursor with
            | RowId.Dir path -> climb path
            | RowId.File(directory, _) -> climb directory

    let private indexOfCursor (state: TreeState) (rows: VisibleRow list) : int =
        let cursor = settle state rows
        rows |> List.tryFindIndex (fun row -> row.Id = cursor) |> Option.defaultValue 0

    let private moveTo (rows: VisibleRow list) (index: int) (state: TreeState) : TreeState =
        match List.tryItem index rows with
        | Some row -> { state with Cursor = row.Id }
        | None -> state

    let private currentDirectory (state: TreeState) (id: RowId) : RollupDirectory option =
        match id with
        | RowId.Dir path -> state.Directories |> List.tryFind (fun directory -> String.Equals(directory.DirectoryPath, path, StringComparison.OrdinalIgnoreCase))
        | RowId.File _ -> None

    /// The state after a key.
    let step (pressed: Key) (state: TreeState) : TreeState =
        let rows = visibleRows state
        let index = indexOfCursor state rows
        let state = { state with Cursor = (settle state rows) }
        let last = rows.Length - 1

        match pressed with
        | Key.Up -> moveTo rows (max 0 (index - 1)) state
        | Key.Down -> moveTo rows (min last (index + 1)) state
        | Key.Home -> moveTo rows 0 state
        | Key.End -> moveTo rows last state
        | Key.Quit -> { state with Finished = true }
        | Key.Other -> state
        | Key.ExpandAll ->
            { state with
                Expanded =
                    state.Directories
                    |> List.filter (isExpandable state)
                    |> List.map (fun directory -> key directory.DirectoryPath)
                    |> Set.ofList }
        | Key.Right ->
            match currentDirectory state state.Cursor with
            | Some directory when isExpandable state directory ->
                if state.Expanded.Contains(key directory.DirectoryPath) then
                    moveTo rows (index + 1) state
                else
                    { state with Expanded = state.Expanded.Add(key directory.DirectoryPath) }
            | _ -> state
        | Key.Left ->
            match currentDirectory state state.Cursor with
            | Some directory when state.Expanded.Contains(key directory.DirectoryPath) && isExpandable state directory ->
                { state with Expanded = state.Expanded.Remove(key directory.DirectoryPath) }
            | _ ->
                match List.tryItem index rows with
                | Some { Parent = ValueSome parent } -> { state with Cursor = parent }
                | _ -> state

    let private hints = "Up/Down move  Right/Left expand/collapse  Home/End  * expand all  q quit"

    /// The lines of one frame for a terminal of the given height, and the state with its scroll offset fitted so the
    /// cursor row is shown and no empty space is left while rows are hidden above. The tree gets the height less the
    /// header and the status line, and at least one row.
    let frame (height: int) (state: TreeState) : TreeState * Display =
        let rows = visibleRows state
        let index = indexOfCursor state rows
        let cursor = rows |> List.tryItem index |> Option.map _.Id |> Option.defaultValue state.Cursor
        let capacity = max 1 (height - state.Header.Length - 1)
        let tooFar = if index < state.Offset then index else state.Offset
        let fitted = if index >= tooFar + capacity then index - capacity + 1 else tooFar
        let offset = max 0 (min fitted (max 0 (rows.Length - capacity)))
        let shown = rows |> List.skip offset |> List.truncate capacity
        let above = offset
        let below = rows.Length - offset - shown.Length

        let lines =
            shown
            |> List.map (fun row ->
                let gutter = if row.Id = cursor then cursorGutter else noGutter
                Line.Spans(plain (gutter + row.Indent + row.Marker) :: row.Label))

        let more =
            [ if above > 0 then $"({above} more above)"
              if below > 0 then $"({below} more below)" ]

        let status = String.Join("  ", more @ [ hints ])

        { state with Cursor = cursor; Offset = offset }, state.Header @ lines @ [ Line.Text(Tone.Neutral, status) ]
