/// `vara prune <profile> [--config <path>] [--yes]`: applies the profile's retention policy. A pipeline over the shared
/// stages: resolve the profile, refuse a profile without a policy, read what is eligible, ask once (or require
/// `--yes`), then run the prune under the target's lock with its progress shown. The profile is positional only;
/// `--profile` is a syntax error.
module Vara.Cli.PruneCommand

#nowarn "3265"

open System
open System.CommandLine
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

/// The snapshots a prune would remove now. Read-only and without the run lock, so it can be stale by the time the
/// prune runs: the run evaluates again and removes only what is both confirmed and still eligible.
let private eligibleNow (profile: Profile) (policy: RetentionPolicy) : int64 list =
    let root = AbsolutePath.value (Profile.target profile)
    let handle = SqliteManifest.openReadOnly (SqliteManifest.pathIn root)

    try
        (Retention.evaluate policy (SqliteManifest.snapshots handle)).Eligible
    finally
        SqliteManifest.close handle

/// Runs the prune on a pool thread, so that the display can redraw meanwhile, with its progress shown.
let private execute
    (env: CliEnv)
    (token: CancellationToken)
    (profile: Profile)
    (policy: RetentionPolicy)
    (confirmed: int64 list voption)
    : Task<Result<PruneResult, BackupRefusal>> =
    let mode = OutputMode.choose false env.Capabilities
    let target = Profile.target profile
    let store = ContentStore.layout (AbsolutePath.value target)

    let run (publish: ProgressEvent -> unit) : Task<Result<PruneResult, BackupRefusal>> =
        Task.Run(fun () ->
            PruneRun.run
                (PruneRun.live store (fun () -> token.IsCancellationRequested) publish)
                (Profile.name profile)
                target
                policy
                confirmed)

    ProgressDisplay.showWith
        env
        mode
        Display.pruneWording
        Tracker.initial
        (fun () -> DateTimeOffset.UtcNow)
        run
        (function
        | Ok result -> PruneView.outcome result.Failed result.Stopped
        | Error _ -> Outcome.HardError)

/// What the user's answer, or the lack of one, allows.
[<RequireQualifiedAccess>]
type private Authorization =
    /// Run, removing only these snapshots when the user was shown a list, or whatever is eligible when none was.
    | Run of confirmed: int64 list voption
    | Declined
    | Refused of message: string

let private authorize (env: CliEnv) (profileName: string) (yes: bool) (eligible: int64 list) : Authorization =
    if eligible.IsEmpty || yes then
        // Nothing was shown to confirm, so the run evaluates eligibility afresh.
        Authorization.Run ValueNone
    elif not env.Capabilities.InputIsInteractive then
        Authorization.Refused(PruneView.confirmationRequired profileName eligible.Length)
    elif env.Prompts.Confirm(PruneView.confirmation eligible.Length) then
        // Exactly the snapshots shown: one that becomes eligible after this point is left for a later run.
        Authorization.Run(ValueSome eligible)
    else
        Authorization.Declined

let create (env: CliEnv) : Command =
    let profileArgument = Argument<string>("profile", Description = "The profile to prune.")
    let configOption = HistorySession.configOption ()

    let yesOption =
        Option<bool>("--yes", "-y")
    yesOption.Description <- "Confirm the run without prompting, even if snapshots are eligible for removal."

    let command =
        Command("prune", "Apply a profile's retention policy: remove expired snapshots and unreferenced content.")

    command.Arguments.Add profileArgument
    command.Options.Add configOption
    command.Options.Add yesOption

    let handler (parseResult: ParseResult) (token: CancellationToken) : Task<Outcome> =
        task {
            let name = parseResult.GetValue profileArgument |> nonNull
            let configPath = ConfigFile.locate (HistorySession.textOf parseResult configOption)

            match ProfileResolution.forProfile configPath name with
            | Error error -> return HistorySession.fail env (ConfigError.render error)
            | Ok profile ->
                match Profile.retention profile with
                | None -> return HistorySession.fail env (PruneView.noPolicy (Profile.name profile))
                | Some policy ->
                    match authorize env (Profile.name profile) (parseResult.GetValue yesOption) (eligibleNow profile policy) with
                    | Authorization.Refused message -> return HistorySession.fail env message
                    | Authorization.Declined ->
                        HistorySession.show env PruneView.declined
                        return Outcome.Success
                    | Authorization.Run confirmed ->
                        let! ran = execute env token profile policy confirmed

                        match ran with
                        | Error refusal -> return HistorySession.fail env (BackupRefusal.renderFor "prune" refusal)
                        | Ok result ->
                            HistorySession.show env (PruneView.summary result.SnapshotsRemoved result.BlobsRemoved result.Failed result.Stopped)
                            return PruneView.outcome result.Failed result.Stopped
        }

    CommandAction.set env.ErrorConsole handler command
