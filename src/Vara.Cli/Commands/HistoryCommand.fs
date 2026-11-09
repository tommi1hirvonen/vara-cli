/// `vara history <path> [--profile <name>] [--config <path>]`: a file's recorded versions, newest first, following a
/// move back through the paths the file had.
module Vara.Cli.HistoryCommand

open System.CommandLine
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

let create (env: CliEnv) : Command =
    let pathArgument =
        Argument<string>(
            "path",
            Description = "The file to show history for - a mirror-relative path, an absolute source path, or a path relative to the current directory."
        )

    let profileOption = HistorySession.profileOption "query"
    let configOption = HistorySession.configOption ()
    let command = Command("history", "List the recorded versions of a file, most recent first.")
    command.Arguments.Add pathArgument
    command.Options.Add profileOption
    command.Options.Add configOption

    let handler (parseResult: ParseResult) (_: CancellationToken) : Task<Outcome> =
        task {
            let raw = parseResult.GetValue pathArgument |> nonNull

            match HistorySession.resolve env (HistorySession.textOf parseResult configOption) (HistorySession.textOf parseResult profileOption) with
            | Error error -> return HistorySession.fail env (ConfigError.render error)
            | Ok target ->
                return
                    HistorySession.withManifest target (fun handle ->
                        let path = HistorySession.tryResolveFile env target handle raw |> ValueOption.defaultValue raw
                        let versions = History.versionsOf (SqliteManifest.versionsOfPath handle) (MirrorPath.ofString path)

                        match versions with
                        | [] -> HistorySession.fail env (HistoryError.render (HistoryError.NoHistory path))
                        | _ ->
                            HistorySession.show env (HistoryView.ofHistory versions)
                            Outcome.Success)
        }

    CommandAction.set env.ErrorConsole handler command
