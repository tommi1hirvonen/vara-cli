/// `vara deleted [directory] [--since <date>]`: the files deleted from a profile, most recently deleted first,
/// optionally within one directory.
module Vara.Cli.DeletedCommand

open System.CommandLine
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

let create (env: CliEnv) : Command =
    let directoryArgument =
        Argument<string | null>(
            "directory",
            Description = "Scope the report to this directory - a mirror-relative path, an absolute source path, or a path relative to the current directory. Defaults to the whole profile.",
            Arity = ArgumentArity.ZeroOrOne
        )

    let profileOption = HistorySession.profileOption "query"
    let configOption = HistorySession.configOption ()

    let sinceOption =
        Option<string | null>("--since", Description = DateInput.help "Only include files deleted at or after this date/time")

    let command = Command("deleted", "Report files deleted from a profile, most recently deleted first.")
    command.Arguments.Add directoryArgument
    command.Options.Add profileOption
    command.Options.Add sinceOption
    command.Options.Add configOption

    let handler (parseResult: ParseResult) (_: CancellationToken) : Task<Outcome> =
        task {
            let directory =
                parseResult.GetValue directoryArgument
                |> Option.ofObj
                |> Option.filter (fun text -> text.Trim().Length > 0)

            match HistorySession.resolve env (HistorySession.textOf parseResult configOption) (HistorySession.textOf parseResult profileOption) with
            | Error error -> return HistorySession.fail env (ConfigError.render error)
            | Ok target ->
                let since =
                    match HistorySession.textOf parseResult sinceOption with
                    | None -> Ok ValueNone
                    | Some text -> HistorySession.parseDate env "--since" text |> Result.map ValueSome

                match since with
                | Error message -> return HistorySession.fail env message
                | Ok since ->
                    return
                        HistorySession.withManifest target (fun handle ->
                            let scope =
                                match directory with
                                | Some raw -> (HistorySession.resolveDirectory env target handle raw).Directory
                                | None -> ""

                            let deleted = History.recentlyDeleted scope since (ManifestQueries.allVersions handle)
                            HistorySession.show env (HistoryView.ofDeleted deleted)
                            Outcome.Success)
        }

    CommandAction.set env.ErrorConsole handler command
