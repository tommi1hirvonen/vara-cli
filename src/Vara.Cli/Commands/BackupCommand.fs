/// `vara backup <profile> [--config <path>] [--dry-run] [--json] [--depth <n>] [--files] [--interactive]`: resolves the
/// profile, runs the engine with its progress shown, and prints the run summary styled by outcome, or one JSON object.
/// A tree option adds the change tree after the summary. The profile is positional only; `--profile` is a syntax error.
module Vara.Cli.BackupCommand

// GetValue<bool> is declared to return a nullable type, which F# cannot express for a value type; the value is never null.
#nowarn "3265"

open System.CommandLine
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

/// The summary of a command that was not refused. In the live mode the headline carries the outcome's colour; in the
/// plain mode nothing is coloured.
let private writeSummary (env: CliEnv) (mode: OutputMode) (display: Display) : unit =
    match mode with
    | OutputMode.Live -> LiveRenderer.print env.Console display
    | OutputMode.Plain
    | OutputMode.Json -> PlainRenderer.write env.Console display

/// The engine's observers, publishing what they hear as progress events. A JSON run has none, and only a live display
/// shows the scan's running count.
let observersFor (mode: OutputMode) (publish: ProgressEvent -> unit) : Observers =
    match mode with
    | OutputMode.Json -> Observers.none
    | OutputMode.Live
    | OutputMode.Plain ->
        { ScanProgress =
            match mode with
            | OutputMode.Live -> ValueSome(fun entries -> publish (ProgressEvent.ScanCounted entries))
            | OutputMode.Plain
            | OutputMode.Json -> ValueNone
          TransferPhaseStarting = fun totals -> publish (ProgressEvent.TransferStarting(totals.Bytes, totals.Files))
          BytesCopied = ValueSome(fun delta -> publish (ProgressEvent.BytesCopied delta))
          FileStarted = fun path -> publish (ProgressEvent.FileStarted path)
          FileFinished = fun path -> publish (ProgressEvent.FileFinished path)
          FileTransferred = fun () -> publish ProgressEvent.FileTransferred }

let private refused (env: CliEnv) (refusal: BackupRefusal) : Outcome =
    ErrorReporting.report env.ErrorConsole (BackupRefusal.render refusal)

let private warn (env: CliEnv) (message: string) : unit =
    Terminal.writeLine env.ErrorConsole Terminal.partialFailureStyle message

/// The tree options of the command. A tree is shown when any of them is given; `--interactive` implies it.
type private TreeRequest =
    { Depth: int voption
      Files: bool
      Interactive: bool }

module private TreeRequest =
    let requested (request: TreeRequest) : bool =
        request.Depth.IsSome || request.Files || request.Interactive

/// A line apart from the summary above it.
let private gap: Display = [ Line.Text(Tone.Plain, "") ]

/// A dry run: the plan is made on a pool thread with the scan shown, and nothing is written. With a tree option the
/// planned changes come back too, and the tree follows the summary once the lock is released.
let private dryRun
    (env: CliEnv)
    (mode: OutputMode)
    (json: bool)
    (tree: TreeRequest)
    (backupEnv: BackupEnv)
    (profile: Profile)
    : Task<Outcome> =
    task {
        let startedAt = backupEnv.Clock()
        let detail = TreeRequest.requested tree

        let! planned =
            ProgressDisplay.show
                env
                mode
                backupEnv.Clock
                (fun publish ->
                    Task.Run(fun () ->
                        BackupRun.dryRunDetailed { backupEnv with Observers = observersFor mode publish } profile detail))
                (function
                | Ok(struct (summary, _)) -> Display.planOutcome summary
                | Error _ -> Outcome.HardError)

        match planned with
        | Error refusal -> return refused env refusal
        | Ok(struct (summary, entries)) ->
            if json then
                JsonOutput.write env.JsonOut (RunJson.ofPlan (Profile.name profile) startedAt (backupEnv.Clock()) summary)
            else
                writeSummary env mode (Display.ofPlan summary)

                if detail && entries.Length > 0 then
                    let target = HistorySession.targetOfProfile profile
                    let browse = ChangeTree.canBrowse env tree.Interactive

                    ChangeTree.present
                        env
                        { StaticHeader = gap
                          InteractiveHeader = [ Line.Text(Tone.Plain, "Dry run - planned changes") ]
                          Scope = ""
                          SourceRoots = target.SourceRoots
                          Depth = tree.Depth
                          Rollup = History.rollupEntries "" (tree.Files && not browse) entries
                          Entries = if browse then entries else Array.empty
                          Interactive = tree.Interactive
                          MayOpenInteractive = true }
                    |> ignore

            return Display.planOutcome summary
    }

/// The tree of the snapshot a real run recorded, after its summary. A failure to read it is reported and leaves the run's
/// outcome as it was. The interactive view is withheld after a cancelled run.
let private showRecordedTree (env: CliEnv) (tree: TreeRequest) (profile: Profile) (cancelled: bool) (result: RunResult) : unit =
    let target = HistorySession.targetOfProfile profile

    let browse = ChangeTree.canBrowse env tree.Interactive && not cancelled

    match ChangeTree.readSnapshot target result.SnapshotId tree.Files browse with
    | Error failure -> warn env $"Could not read the change tree of snapshot #{result.SnapshotId}: {failure.Message}"
    | Ok ValueNone -> ()
    | Ok(ValueSome(snapshot, rollup, entries)) ->
        match rollup with
        | [] -> HistorySession.show env [ Line.Text(Tone.Neutral, $"No changes recorded for snapshot #{result.SnapshotId}.") ]
        | _ ->
            ChangeTree.present
                env
                { StaticHeader = gap
                  InteractiveHeader = HistoryView.snapshotHeader snapshot
                  Scope = ""
                  SourceRoots = target.SourceRoots
                  Depth = tree.Depth
                  Rollup = rollup
                  Entries = entries
                  Interactive = tree.Interactive
                  MayOpenInteractive = not cancelled }
            |> ignore

/// A real run, with Windows kept awake: a run that cannot be started because of a refusal is a hard error.
let private realRun
    (env: CliEnv)
    (mode: OutputMode)
    (json: bool)
    (tree: TreeRequest)
    (cancelled: unit -> bool)
    (backupEnv: BackupEnv)
    (profile: Profile)
    : Task<Outcome> =
    task {
        let startedAt = backupEnv.Clock()

        let! ran =
            PowerRequest.withSleepPrevention env.Power (warn env) (fun () ->
                ProgressDisplay.show
                    env
                    mode
                    backupEnv.Clock
                    (fun publish -> BackupRun.run { backupEnv with Observers = observersFor mode publish } profile)
                    (function
                    | Ok result -> Run.outcome result
                    | Error _ -> Outcome.HardError))

        match ran with
        | Error refusal -> return refused env refusal
        | Ok result ->
            let completedAt = backupEnv.Clock()

            if json then
                JsonOutput.write env.JsonOut (RunJson.ofRun (Profile.name profile) startedAt completedAt result)
            else
                PlainRenderer.writeLine env.Console ""
                writeSummary env mode (Display.ofRun (completedAt - startedAt) result)

                if TreeRequest.requested tree then
                    showRecordedTree env tree profile (cancelled () || result.Status = SnapshotStatus.Cancelled) result

            return Run.outcome result
    }

/// The command.
let create (env: CliEnv) : Command =
    let profileArgument = Argument<string>("profile", Description = "The profile to back up.")

    let configOption =
        Option<string | null>(
            "--config",
            Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)."
        )

    let dryRunOption =
        Option<bool>(
            "--dry-run",
            Description =
                "Preview the planned added/changed/moved/deleted changes and bytes to transfer without performing any writes."
        )

    let jsonOption =
        Option<bool>(
            "--json",
            Description =
                "Print a single machine-readable JSON summary of the outcome to stdout instead of the human-oriented output, and suppress the progress display."
        )

    let depthOption =
        Option<string | null>(
            "--depth",
            Description =
                "Show the change tree after the summary, limited to this many directory levels below the mirror root (0 shows only the root)."
        )

    let filesOption =
        Option<bool>("--files", Description = "Show the change tree after the summary, including each changed file path and kind.")

    let interactiveOption =
        Option<bool>(
            "--interactive",
            Description =
                "Browse the change tree with the keyboard after the summary: Up/Down move, Right/Left expand and collapse, Home/End jump, * expands all, d shows or hides deleted files, f shows or hides all files, q or Esc quits. Lists the changed files whether or not --files is given. Starts at --depth levels (default 1)."
        )

    let command = Command("backup", "Run an incremental backup for a profile.")
    command.Arguments.Add profileArgument
    command.Options.Add configOption
    command.Options.Add dryRunOption
    command.Options.Add jsonOption
    command.Options.Add depthOption
    command.Options.Add filesOption
    command.Options.Add interactiveOption

    let handler (parseResult: ParseResult) (token: CancellationToken) : Task<Outcome> =
        task {
            let name = parseResult.GetValue profileArgument |> nonNull
            let json = parseResult.GetValue jsonOption
            let depthText = parseResult.GetValue depthOption |> Option.ofObj

            // Checked before the profile is resolved, so that nothing is scanned or run for a refused combination.
            match ChangeTree.parseDepth depthText with
            | Error message -> return ErrorReporting.usage env.ErrorConsole message
            | Ok depth ->
                let tree =
                    { Depth = depth
                      Files = parseResult.GetValue filesOption
                      Interactive = parseResult.GetValue interactiveOption }

                if json && TreeRequest.requested tree then
                    return ErrorReporting.usage env.ErrorConsole "--json cannot be combined with --depth, --files or --interactive."
                else
                    let configPath = ConfigFile.locate (parseResult.GetValue configOption |> Option.ofObj)

                    match ProfileResolution.forProfile configPath name with
                    | Error error -> return ErrorReporting.report env.ErrorConsole (ConfigError.render error)
                    | Ok profile ->
                        let backupEnv = env.MakeBackupEnv(fun () -> token.IsCancellationRequested)
                        let mode = OutputMode.choose json env.Capabilities

                        if parseResult.GetValue dryRunOption then
                            return! dryRun env mode json tree backupEnv profile
                        else
                            return! realRun env mode json tree (fun () -> token.IsCancellationRequested) backupEnv profile
        }

    CommandAction.set env.ErrorConsole handler command
