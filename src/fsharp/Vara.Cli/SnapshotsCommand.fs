/// `vara snapshots [profile] [--config <path>]`: lists the recorded snapshots of a profile as an aligned table. The
/// profile is positional only; with none given, the working directory decides. Read-only: a profile that never ran a
/// backup has no storage, and none is created.
module Vara.Cli.SnapshotsCommand

open System.CommandLine
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

let create (env: CliEnv) : Command =
    let profileArgument =
        Argument<string | null>(
            "profile",
            Description = "The profile whose snapshots to list. Optional when the current directory is inside a profile's target root.",
            Arity = ArgumentArity.ZeroOrOne
        )

    let configOption = HistorySession.configOption ()
    let command = Command("snapshots", "List recorded snapshots for a profile.")
    command.Arguments.Add profileArgument
    command.Options.Add configOption

    let handler (parseResult: ParseResult) (_: CancellationToken) : Task<Outcome> =
        task {
            let name = parseResult.GetValue profileArgument |> Option.ofObj
            let configPath = HistorySession.textOf parseResult configOption

            match HistorySession.resolve env configPath name with
            | Error error -> return HistorySession.fail env (ConfigError.render error)
            | Ok target ->
                let snapshots = HistorySession.withManifest target SqliteManifest.snapshots
                HistorySession.show env (HistoryView.ofSnapshots snapshots)
                return Outcome.Success
        }

    CommandAction.set env.ErrorConsole handler command
