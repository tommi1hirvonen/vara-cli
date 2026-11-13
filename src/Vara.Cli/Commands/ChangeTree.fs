/// What `snapshot` and `backup` share when they show a change tree: the `--depth` value and the step that either
/// opens the interactive tree or prints the static one.
module Vara.Cli.ChangeTree

open System
open System.Globalization
open Vara.Domain
open Vara.Engine

/// The text of `--depth` as a depth: none when the option is absent, or the message that rejects a value that is not a
/// non-negative integer.
let parseDepth (text: string option) : Result<int voption, string> =
    match text with
    | None -> Ok ValueNone
    | Some text ->
        match Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, depth -> Ok(ValueSome depth)
        | false, _ -> Error "--depth must be a non-negative integer."

/// The part of an option description that says how the interactive tree is driven, shared by the commands that offer it.
let interactiveHelp: string =
    "Up/Down move, Right/Left expand and collapse, Home/End jump, * expands all, d shows or hides deleted files, f shows or hides all files, q or Esc quits. Lists the changed files whether or not --files is given. Starts at --depth levels (default 1)."

/// What a command does with the tree, decided before the changes are read so that a tree that falls back to the static
/// view is read for the static view.
[<RequireQualifiedAccess>]
type Mode =
    /// Print the static tree.
    | Static
    /// `--interactive` was given and cannot be honoured: print the static tree with a note.
    | Unavailable
    /// Open the interactive tree.
    | Browse

/// The mode for a command: `interactive` is whether `--interactive` was given, and `allowed` is false where the
/// interactive view is withheld, such as after a cancelled run. A withheld view falls back without a note.
let modeOf (env: CliEnv) (interactive: bool) (allowed: bool) : Mode =
    if not (interactive && allowed) then Mode.Static
    elif HistorySession.canPrompt env then Mode.Browse
    else Mode.Unavailable

/// How a tree is shown. The interactive tree lists files, so it carries every change under its scope as an entry.
[<RequireQualifiedAccess>]
type Viewing =
    | Static
    | Unavailable
    | Browse of entries: ChangeEntry[]

/// The viewing for a mode, given the entries that the interactive tree lists.
let viewingOf (mode: Mode) (entries: ChangeEntry[]) : Viewing =
    match mode with
    | Mode.Static -> Viewing.Static
    | Mode.Unavailable -> Viewing.Unavailable
    | Mode.Browse -> Viewing.Browse entries

/// A snapshot's changes under a directory, as the tree shows them. The static tree reads the rollup, with file lists when
/// `includeFiles`. The interactive tree reads every change as an entry, so that it can list files whatever
/// `includeFiles` says, and the rollup then holds the counts only.
let loadChanges
    (handle: ManifestHandle)
    (snapshotId: int64)
    (directory: string)
    (includeFiles: bool)
    (mode: Mode)
    : RollupDirectory list * Viewing =
    match mode with
    | Mode.Browse ->
        let entries =
            ManifestQueries.snapshotVersions handle snapshotId
            |> Seq.map History.entryOf
            |> History.entriesUnder directory

        History.rollupEntries directory false entries, Viewing.Browse entries
    | Mode.Static
    | Mode.Unavailable ->
        History.rollup directory includeFiles (ManifestQueries.snapshotVersions handle snapshotId), viewingOf mode Array.empty

/// A recorded snapshot and its changes under the mirror root.
type RecordedChanges =
    { Snapshot: SnapshotInfo
      Rollup: RollupDirectory list
      Viewing: Viewing }

/// A snapshot and its changes read from the manifest, none when the snapshot is not recorded, or the failure that
/// stopped the read. The manifest is opened read-only and closed before the result is returned.
let readSnapshot
    (target: HistorySession.Target)
    (snapshotId: int64)
    (includeFiles: bool)
    (mode: Mode)
    : Result<RecordedChanges voption, exn> =
    try
        HistorySession.withManifest target (fun handle ->
            match ManifestQueries.snapshots handle |> List.tryFind (fun snapshot -> snapshot.SnapshotId = snapshotId) with
            | None -> Ok ValueNone
            | Some snapshot ->
                let rollup, viewing = loadChanges handle snapshotId "" includeFiles mode

                Ok(
                    ValueSome
                        { Snapshot = snapshot
                          Rollup = rollup
                          Viewing = viewing }
                ))
    with failure ->
        Error failure

/// What to show of a change tree and how.
type Presentation =
    { /// Printed above the static tree.
      StaticHeader: Display
      /// Shown above the interactive tree.
      InteractiveHeader: Display
      Scope: string
      SourceRoots: Set<string>
      Depth: int voption
      Rollup: RollupDirectory list
      Viewing: Viewing }

let private showStatic (env: CliEnv) (presentation: Presentation) : unit =
    HistorySession.show
        env
        (presentation.StaticHeader
         @ HistoryView.treeLines presentation.Scope presentation.SourceRoots presentation.Depth presentation.Rollup)

/// Opens the interactive tree when the viewing is `Browse`, and otherwise prints the static tree, with one line on
/// standard error when the interactive view was asked for and is unavailable. The outcome of a browsing session when
/// one ran, and none otherwise.
let present (env: CliEnv) (presentation: Presentation) : Outcome voption =
    match presentation.Viewing with
    | Viewing.Browse entries ->
        let state =
            TreeBrowser.create
                presentation.InteractiveHeader
                presentation.Scope
                presentation.SourceRoots
                presentation.Depth
                presentation.Rollup
                entries

        ValueSome(TreeSession.run env state)
    | Viewing.Unavailable ->
        Terminal.writeLine env.ErrorConsole Terminal.neutralStyle "The interactive view is unavailable here; showing the static tree."
        showStatic env presentation
        ValueNone
    | Viewing.Static ->
        showStatic env presentation
        ValueNone
