/// `vara restore <path> (--out <path> | --in-place) [--at <date> | --version <id>] [--recursive [--snapshot <id>]]
/// [--force]`: restores a file, or with `--recursive` a directory, as it was, never into the live mirror. A restore is
/// a plan and then an execution: the pure plan says what is written and removed, the guard and the confirmation read
/// it, and the shell performs it atomically with its progress shown.
module Vara.Cli.RestoreCommand

#nowarn "3265"

open System
open System.CommandLine
open System.IO
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

/// What the user asked for, after the options are read.
type private Request =
    { Path: string
      Out: string option
      InPlace: bool
      At: string option
      Version: int64 voption
      Snapshot: int64 voption
      Force: bool
      Recursive: bool }

let private destinationOf (request: Request) : Destination =
    match request.Out with
    | Some path -> Destination.Explicit path
    | None -> Destination.InPlace

/// The point in time a restore is to match, or the reason it cannot be named.
let private momentOf (env: CliEnv) (request: Request) : Result<DateTimeOffset voption, string> =
    match request.At with
    | None -> Ok ValueNone
    | Some text -> HistorySession.parseDate env "--at" text |> Result.map ValueSome

/// Applies the plan with progress shown. The writes run on a pool thread so the display can redraw meanwhile.
let private execute (env: CliEnv) (token: CancellationToken) (target: HistorySession.Target) (label: string) (plan: RestorePlan) : Task<RestoreResult> =
    let mode = OutputMode.choose false env.Capabilities

    let run (publish: ProgressEvent -> unit) : Task<RestoreResult> =
        Task.Run(fun () ->
            RestoreRun.execute
                { OpenBlob = ContentStore.openRead target.Layout
                  ShouldStop = fun () -> token.IsCancellationRequested
                  Publish = publish }
                plan)

    ProgressDisplay.showRestore
        env
        mode
        (fun () -> DateTimeOffset.UtcNow)
        label
        plan
        run
        (fun result -> if result.Failed.IsEmpty && not result.Stopped then Outcome.Success else Outcome.PartialFailure)

/// The paths a restore could not write, one per line under a heading.
let private failureLines (failures: RestoreFailure list) : Display =
    Line.Text(Tone.Plain, $"  Failed ({failures.Length}):")
    :: (failures |> List.map (fun failure -> Line.Text(Tone.Plain, $"    - {failure.Destination}: {failure.Reason}")))

let private restoreFile
    (env: CliEnv)
    (token: CancellationToken)
    (target: HistorySession.Target)
    (handle: ManifestHandle)
    (request: Request)
    : Task<Outcome> =
    task {
        let resolved = HistorySession.tryResolveFile env target handle request.Path

        // No file matched: a tracked directory gets its own, actionable message instead of "no history".
        let directory =
            match resolved with
            | ValueSome _ -> ValueNone
            | ValueNone -> HistorySession.tryResolveTrackedDirectory env target handle request.Path

        match directory with
        | ValueSome path -> return HistorySession.fail env (HistoryError.render (HistoryError.IsDirectory path))
        | ValueNone ->
            let path = resolved |> ValueOption.defaultValue request.Path
            let history = History.versionsOf (SqliteManifest.versionsOfPath handle) (MirrorPath.ofString path)

            // Neither a version nor a date: offer the path's restorable versions (the checks already ruled out a
            // session that cannot be asked).
            let selected: Result<VersionSelector, string> =
                match request.Version with
                | ValueSome versionId -> Ok(VersionSelector.ById versionId)
                | ValueNone ->
                    match momentOf env request with
                    | Error message -> Error message
                    | Ok(ValueSome time) -> Ok(VersionSelector.AsOf time)
                    | Ok ValueNone ->
                        match History.restorableVersions history with
                        | [] -> Error(HistoryError.render (HistoryError.NoHistory path))
                        | choices ->
                            let index = env.Prompts.Select "Select a version to restore:" (choices |> List.map HistoryView.versionChoice) 0
                            Ok(VersionSelector.ById choices[index].VersionId)

            let plan =
                selected
                |> Result.bind (fun selector -> History.resolveVersion path selector history |> Result.mapError HistoryError.render)
                |> Result.bind (fun version -> Restore.planFile path (destinationOf request) version |> Result.mapError HistoryError.render)

            match plan with
            | Error message -> return HistorySession.fail env message
            | Ok plan ->
                let destination = plan.Writes.Head.Destination

                match RestoreRun.checkDestinations target.Layout plan with
                | Error error -> return HistorySession.fail env (HistoryError.render error)
                | Ok() ->
                    // An existing file is overwritten only when the user said so, by --force or by answering a prompt.
                    let overwrite =
                        if not (File.Exists destination) || request.Force then
                            Ok true
                        elif env.Capabilities.InputIsInteractive then
                            Ok(env.Prompts.Confirm(HistoryView.overwritePrompt destination))
                        else
                            Error(HistoryError.render (HistoryError.DestinationExists destination))

                    match overwrite with
                    | Error message -> return HistorySession.fail env message
                    | Ok false ->
                        HistorySession.show env HistoryView.restoreDeclined
                        return Outcome.Success
                    | Ok true ->
                        let! result = execute env token target path plan

                        match result.Failed with
                        | failure :: _ ->
                            return
                                HistorySession.fail env $"Could not restore '{path}' to '{failure.Destination}': {failure.Reason}"
                        | [] ->
                            HistorySession.show env (HistoryView.restoredFile path destination)
                            return Outcome.Success
    }

let private restoreDirectory
    (env: CliEnv)
    (token: CancellationToken)
    (target: HistorySession.Target)
    (handle: ManifestHandle)
    (request: Request)
    : Task<Outcome> =
    task {
        let resolution = HistorySession.resolveDirectory env target handle request.Path

        if resolution.FellBackToMirrorRoot then
            HistorySession.show env HistoryView.mirrorRootFallbackForRestore

        let directory = resolution.Directory

        let candidates () : DirectoryCandidate list =
            History.directoryCandidates directory (SqliteManifest.snapshots handle) (SqliteManifest.allVersions handle)

        // A snapshot id, a date, a choice from the picker, or the current state.
        let moment: Result<Moment, string> =
            match request.Snapshot, request.At with
            | ValueSome snapshotId, _ ->
                match candidates () |> List.tryFind (fun candidate -> candidate.Snapshot.SnapshotId = snapshotId) with
                | Some _ -> Ok(Moment.AtSnapshot snapshotId)
                | None -> Error(HistoryError.render (HistoryError.NoMatchingDirectorySnapshot(directory, snapshotId)))
            | ValueNone, Some _ -> momentOf env request |> Result.map (fun time -> Moment.AtTime time.Value)
            | ValueNone, None ->
                if HistorySession.canPrompt env then
                    let offered = candidates ()
                    let choices = HistoryView.currentStateChoice :: (offered |> List.map HistoryView.directoryChoice)

                    match env.Prompts.Select "Select a point in time to restore:" choices 0 with
                    | 0 -> Ok Moment.Latest
                    | index -> Ok(Moment.AtSnapshot offered[index - 1].Snapshot.SnapshotId)
                else
                    Ok Moment.Latest

        let plan =
            moment
            |> Result.bind (fun moment ->
                Restore.planDirectory directory moment (destinationOf request) (SqliteManifest.allVersions handle)
                |> Result.mapError HistoryError.render)
            |> Result.map (Restore.keepExistingRemovals File.Exists)

        match plan with
        | Error message -> return HistorySession.fail env message
        | Ok plan ->
            match RestoreRun.checkDestinations target.Layout plan with
            | Error error -> return HistorySession.fail env (HistoryError.render error)
            | Ok() ->
                // One confirmation for the whole operation, in place of a prompt per file.
                let confirmed: Result<bool, string> =
                    if request.Force then
                        Ok true
                    elif env.Capabilities.InputIsInteractive then
                        Ok(env.Prompts.Confirm(HistoryView.confirmation directory plan))
                    else
                        Error(
                            HistoryError.render (
                                HistoryError.DirectoryConfirmationRequired(directory, plan.Writes.Length, plan.Removals.Length)
                            )
                        )

                match confirmed with
                | Error message -> return HistorySession.fail env message
                | Ok false ->
                    HistorySession.show env HistoryView.restoreDeclined
                    return Outcome.Success
                | Ok true ->
                    let! result = execute env token target directory plan

                    if result.Stopped then
                        // A graceful stop: what was applied stays applied, reported as the partial result it is.
                        HistorySession.show env (HistoryView.restoreStoppedEarly directory)
                        return Outcome.PartialFailure
                    else
                        HistorySession.show env (HistoryView.restoredDirectory directory result.Written result.Removed plan.Skipped.Length)

                        match result.Failed with
                        | [] -> return Outcome.Success
                        | failures ->
                            HistorySession.show env (failureLines failures)
                            return Outcome.PartialFailure
    }

let create (env: CliEnv) : Command =
    let pathArgument =
        Argument<string>(
            "path",
            Description = "The file (or, with --recursive, directory) to restore - a mirror-relative path, an absolute source path, or a path relative to the current directory."
        )

    let profileOption = HistorySession.profileOption "restore from"
    let configOption = HistorySession.configOption ()

    let outOption =
        Option<string | null>("--out", Description = "Destination path to write the restored content to. Mutually exclusive with --in-place.")

    let inPlaceOption =
        Option<bool>(
            "--in-place",
            Description = "Restore back to the original source location instead of an explicit --out destination. Mutually exclusive with --out."
        )

    let atOption =
        Option<string | null>(
            "--at",
            Description =
                DateInput.help "Restore the version current as of this date/time"
                + " With --recursive, omitting this (and --snapshot) restores the directory's current tracked state in a non-interactive session, or presents a snapshot picker interactively. Mutually exclusive with --snapshot."
        )

    let versionOption =
        Option<Nullable<int64>>(
            "--version",
            Description = "Restore this specific version id (see the 'history' command). If neither this nor --at is given in an interactive session, a version picker is shown instead. Mutually exclusive with --recursive."
        )

    let forceOption =
        Option<bool>(
            "--force",
            Description = "Skip the overwrite confirmation (single-file restore) or the single directory-restore confirmation (--recursive) without prompting."
        )

    let recursiveOption =
        Option<bool>(
            "--recursive",
            Description = "Restore an entire directory (subtree) instead of a single file, reconstructing its exact tracked state as of --at/--snapshot (or the current state if neither is given). Mutually exclusive with --version."
        )

    let snapshotOption =
        Option<Nullable<int64>>(
            "--snapshot",
            Description = "Restore the directory's tracked state as of this specific snapshot id (see the 'snapshots' command) - the non-interactive equivalent of selecting it from the --recursive snapshot picker. Only valid with --recursive. Mutually exclusive with --at."
        )

    let command = Command("restore", "Restore a file's historical content, without touching the live mirror.")
    command.Arguments.Add pathArgument
    command.Options.Add profileOption
    command.Options.Add outOption
    command.Options.Add inPlaceOption
    command.Options.Add atOption
    command.Options.Add versionOption
    command.Options.Add configOption
    command.Options.Add forceOption
    command.Options.Add recursiveOption
    command.Options.Add snapshotOption

    let handler (parseResult: ParseResult) (token: CancellationToken) : Task<Outcome> =
        task {
            let idOf (value: Nullable<int64>) : int64 voption = if value.HasValue then ValueSome value.Value else ValueNone

            let request =
                { Path = parseResult.GetValue pathArgument |> nonNull
                  Out = HistorySession.textOf parseResult outOption
                  InPlace = parseResult.GetValue inPlaceOption
                  At = HistorySession.textOf parseResult atOption
                  Version = idOf (parseResult.GetValue versionOption)
                  Snapshot = idOf (parseResult.GetValue snapshotOption)
                  Force = parseResult.GetValue forceOption
                  Recursive = parseResult.GetValue recursiveOption }

            // Whether a question can be asked: a real input stream and a console that can show the picker.
            let canPrompt = HistorySession.canPrompt env

            let refusal =
                HistorySession.firstRefusal
                    [ fun () -> HistorySession.exactlyOne env request.Out.IsSome request.InPlace "--out <path>" "--in-place"
                      fun () -> HistorySession.mutuallyExclusive env request.Recursive request.Version.IsSome "--recursive" "--version"
                      fun () -> HistorySession.mutuallyExclusive env request.At.IsSome request.Version.IsSome "--at" "--version"
                      fun () -> HistorySession.mutuallyExclusive env request.Snapshot.IsSome request.At.IsSome "--snapshot" "--at"
                      fun () ->
                          if request.Snapshot.IsSome && not request.Recursive then
                              ValueSome(
                                  ErrorReporting.usage
                                      env.ErrorConsole
                                      "--snapshot only applies to a recursive directory restore (--recursive)."
                              )
                          else
                              ValueNone
                      fun () ->
                          if not request.Recursive && request.At.IsNone && request.Version.IsNone && not canPrompt then
                              ValueSome(ErrorReporting.usage env.ErrorConsole "specify either --at <date> or --version <id>.")
                          else
                              ValueNone ]

            match refusal with
            | ValueSome outcome -> return outcome
            | ValueNone ->
                match HistorySession.resolve env (HistorySession.textOf parseResult configOption) (HistorySession.textOf parseResult profileOption) with
                | Error error -> return HistorySession.fail env (ConfigError.render error)
                | Ok target ->
                    // The manifest stays open only while the plan is made; the restore itself reads blobs, not rows.
                    let handle = SqliteManifest.openReadOnly (SqliteManifest.pathIn target.Layout.MirrorRoot)

                    try
                        if request.Recursive then
                            return! restoreDirectory env token target handle request
                        else
                            return! restoreFile env token target handle request
                    finally
                        SqliteManifest.close handle
        }

    CommandAction.set env.ErrorConsole handler command
