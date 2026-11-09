/// `vara check <profile> [--config <path>] [--quick]`: verifies the content stored in the profile's target against its
/// manifest, read-only. A pipeline over the shared stages: resolve the profile, run the check with its progress shown,
/// report. The profile is positional only; `--profile` is a syntax error.
module Vara.Cli.CheckCommand

#nowarn "3265"

open System
open System.CommandLine
open System.IO
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

/// Runs the check on a pool thread, so that the display can redraw meanwhile. The manifest is opened read-only and
/// only for the check: a target without one reads as having nothing recorded, and none is created.
let private execute (env: CliEnv) (token: CancellationToken) (profile: Profile) (quick: bool) : Task<CheckResult> =
    let mode = OutputMode.choose false env.Capabilities
    let root = AbsolutePath.value (Profile.target profile)
    let store = ContentStore.layout root

    let run (publish: ProgressEvent -> unit) : Task<CheckResult> =
        Task.Run(fun () ->
            let handle = SqliteManifest.openReadOnly (Path.Combine(root, ".vara", "profile.db"))

            try
                CheckRun.execute (CheckRun.live store (fun () -> token.IsCancellationRequested) publish) store handle quick
            finally
                SqliteManifest.close handle)

    ProgressDisplay.showWith
        env
        mode
        (if quick then Display.quickCheckWording else Display.checkWording)
        Tracker.initial
        (fun () -> DateTimeOffset.UtcNow)
        run
        Check.outcome

let create (env: CliEnv) : Command =
    let profileArgument = Argument<string>("profile", Description = "The profile to check.")
    let configOption = HistorySession.configOption ()

    let quickOption = Option<bool>("--quick")

    quickOption.Description <-
        "Only check that each referenced blob is physically present in the store, without re-reading and re-hashing its content."

    let command =
        Command("check", "Verify that content physically stored in a profile's target still matches the manifest.")

    command.Arguments.Add profileArgument
    command.Options.Add configOption
    command.Options.Add quickOption

    let handler (parseResult: ParseResult) (token: CancellationToken) : Task<Outcome> =
        task {
            let name = parseResult.GetValue profileArgument |> nonNull
            let configPath = ConfigFile.locate (HistorySession.textOf parseResult configOption)

            match ProfileResolution.forProfile configPath name with
            | Error error -> return HistorySession.fail env (ConfigError.render error)
            | Ok profile ->
                let! result = execute env token profile (parseResult.GetValue quickOption)
                HistorySession.show env (CheckView.report result)
                return Check.outcome result
        }

    CommandAction.set env.ErrorConsole handler command
