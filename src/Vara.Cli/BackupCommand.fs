/// `vara backup <profile> [--config <path>] [--dry-run] [--json]`: resolves the profile, runs the engine with its
/// progress shown, and prints the run summary styled by outcome, or one JSON object. The profile is positional only;
/// `--profile` is a syntax error.
module Vara.Cli.BackupCommand

// GetValue<bool> is declared to return a nullable type, which F# cannot express for a value type; the value is never null.
#nowarn "3265"

open System
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

/// A dry run: the plan is made on a pool thread with the scan shown, and nothing is written.
let private dryRun (env: CliEnv) (mode: OutputMode) (json: bool) (backupEnv: BackupEnv) (profile: Profile) : Task<Outcome> =
    task {
        let startedAt = backupEnv.Clock()

        let! planned =
            ProgressDisplay.show
                env
                mode
                backupEnv.Clock
                (fun publish -> Task.Run(fun () -> BackupRun.dryRun { backupEnv with Observers = observersFor mode publish } profile))
                (function
                | Ok summary -> Display.planOutcome summary
                | Error _ -> Outcome.HardError)

        match planned with
        | Error refusal -> return refused env refusal
        | Ok summary ->
            if json then
                JsonOutput.write env.JsonOut (RunJson.ofPlan (Profile.name profile) startedAt (backupEnv.Clock()) summary)
            else
                writeSummary env mode (Display.ofPlan summary)

            return Display.planOutcome summary
    }

/// A real run, with Windows kept awake: a run that cannot be started because of a refusal is a hard error.
let private realRun (env: CliEnv) (mode: OutputMode) (json: bool) (backupEnv: BackupEnv) (profile: Profile) : Task<Outcome> =
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

    let command = Command("backup", "Run an incremental backup for a profile.")
    command.Arguments.Add profileArgument
    command.Options.Add configOption
    command.Options.Add dryRunOption
    command.Options.Add jsonOption

    let handler (parseResult: ParseResult) (token: CancellationToken) : Task<Outcome> =
        task {
            let name = parseResult.GetValue profileArgument |> nonNull
            let configPath = ConfigFile.locate (parseResult.GetValue configOption |> Option.ofObj)

            match ProfileResolution.forProfile configPath name with
            | Error error -> return ErrorReporting.report env.ErrorConsole (ConfigError.render error)
            | Ok profile ->
                let backupEnv = env.MakeBackupEnv(fun () -> token.IsCancellationRequested)
                let json = parseResult.GetValue jsonOption
                let mode = OutputMode.choose json env.Capabilities

                if parseResult.GetValue dryRunOption then
                    return! dryRun env mode json backupEnv profile
                else
                    return! realRun env mode json backupEnv profile
        }

    CommandAction.set env.ErrorConsole handler command
