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

/// Whether the interactive view will open: `--interactive` was given and the session can be driven by keys. Decided
/// before the changes are read, so that a tree that falls back to the static view is read for the static view.
let canBrowse (env: CliEnv) (interactive: bool) : bool = interactive && HistorySession.canPrompt env

/// A snapshot's changes under a directory, as the tree shows them. The static tree reads the rollup, with file lists when
/// `includeFiles`. The interactive tree reads every change as an entry, so that it can list files whatever
/// `includeFiles` says, and the rollup then holds the counts only.
let loadChanges
    (handle: ManifestHandle)
    (snapshotId: int64)
    (directory: string)
    (includeFiles: bool)
    (interactive: bool)
    : RollupDirectory list * ChangeEntry[] =
    if interactive then
        let entries =
            ManifestQueries.snapshotVersions handle snapshotId
            |> Seq.map History.entryOf
            |> History.entriesUnder directory

        History.rollupEntries directory false entries, entries
    else
        History.rollup directory includeFiles (ManifestQueries.snapshotVersions handle snapshotId), Array.empty

/// A snapshot and its changes read from the manifest, none when the snapshot is not recorded, or the failure that
/// stopped the read. The manifest is opened read-only and closed before the result is returned.
let readSnapshot
    (target: HistorySession.Target)
    (snapshotId: int64)
    (includeFiles: bool)
    (interactive: bool)
    : Result<(SnapshotInfo * RollupDirectory list * ChangeEntry[]) voption, exn> =
    try
        HistorySession.withManifest target (fun handle ->
            match ManifestQueries.snapshots handle |> List.tryFind (fun snapshot -> snapshot.SnapshotId = snapshotId) with
            | None -> Ok ValueNone
            | Some snapshot ->
                let rollup, entries = loadChanges handle snapshotId "" includeFiles interactive
                Ok(ValueSome(snapshot, rollup, entries)))
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
      /// Every change under the scope, for the interactive tree to list as files. Empty when it is not needed.
      Entries: ChangeEntry[]
      /// Whether `--interactive` was given.
      Interactive: bool
      /// Whether the interactive view may open at all. When it may not, the static tree is shown without a note.
      MayOpenInteractive: bool }

/// Opens the interactive tree where it can be driven by keys, and otherwise prints the static tree, with one line on
/// standard error when the interactive view was asked for and is unavailable. The outcome of a browsing session when
/// one ran, and none otherwise.
let present (env: CliEnv) (presentation: Presentation) : Outcome voption =
    if presentation.Interactive && presentation.MayOpenInteractive && HistorySession.canPrompt env then
        let state =
            TreeBrowser.create
                presentation.InteractiveHeader
                presentation.Scope
                presentation.SourceRoots
                presentation.Depth
                presentation.Rollup
                presentation.Entries

        ValueSome(TreeSession.run env state)
    else
        if presentation.Interactive && presentation.MayOpenInteractive then
            Terminal.writeLine env.ErrorConsole Terminal.neutralStyle "The interactive view is unavailable here; showing the static tree."

        HistorySession.show
            env
            (presentation.StaticHeader
             @ HistoryView.treeLines presentation.Scope presentation.SourceRoots presentation.Depth presentation.Rollup)

        ValueNone
