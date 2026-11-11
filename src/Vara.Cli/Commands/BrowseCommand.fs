/// `vara browse [directory] [--at <date>] [--deleted]`: the immediate contents of a directory of the mirror, now or at
/// a point in time, optionally with the entries deleted from it marked and interleaved.
module Vara.Cli.BrowseCommand

#nowarn "3265"

open System.CommandLine
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

let create (env: CliEnv) : Command =
    let directoryArgument =
        Argument<string>(
            "directory",
            Description = "The directory to list - a mirror-relative path, an absolute source path, or a path relative to the current directory. Defaults to the mirror root.",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = (fun _ -> ".")
        )

    let profileOption = HistorySession.profileOption "browse"
    let configOption = HistorySession.configOption ()

    let atOption =
        Option<string | null>("--at", Description = DateInput.help "List the directory's contents as of this date/time instead of now")

    let deletedOption =
        Option<bool>("--deleted", Description = "Include deleted entries, interleaved among live ones and visually marked.")

    let command = Command("browse", "List the immediate (one-level) contents of a directory within a profile's mirror.")
    command.Arguments.Add directoryArgument
    command.Options.Add profileOption
    command.Options.Add atOption
    command.Options.Add deletedOption
    command.Options.Add configOption

    let handler (parseResult: ParseResult) (_: CancellationToken) : Task<Outcome> =
        task {
            let raw = parseResult.GetValue directoryArgument |> nonNull
            let includeDeleted = parseResult.GetValue deletedOption

            match HistorySession.resolve env (HistorySession.textOf parseResult configOption) (HistorySession.textOf parseResult profileOption) with
            | Error error -> return HistorySession.fail env (ConfigError.render error)
            | Ok target ->
                let moment =
                    match HistorySession.textOf parseResult atOption with
                    | None -> Ok Moment.Latest
                    | Some text -> HistorySession.parseDate env "--at" text |> Result.map Moment.AtTime

                match moment with
                | Error message -> return HistorySession.fail env message
                | Ok moment ->
                    return
                        HistorySession.withManifest target (fun handle ->
                            let resolution = HistorySession.resolveDirectory env target handle raw

                            match History.listing resolution.Directory moment includeDeleted (ManifestQueries.allVersions handle) with
                            | Error error -> HistorySession.fail env (HistoryError.render error)
                            | Ok entries ->
                                HistorySession.show env (HistoryView.ofListing entries)

                                if resolution.FellBackToMirrorRoot then
                                    HistorySession.show env HistoryView.mirrorRootFallback

                                Outcome.Success)
        }

    CommandAction.set env.ErrorConsole handler command
