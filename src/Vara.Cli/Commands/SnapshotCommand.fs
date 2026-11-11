/// `vara snapshot <id> [directory] [--files] [--depth <n>]`: what one snapshot changed, as a sparse directory tree
/// with each directory's counts rolled up by change kind.
module Vara.Cli.SnapshotCommand

#nowarn "3265"

open System
open System.CommandLine
open System.Globalization
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

let create (env: CliEnv) : Command =
    let idArgument = Argument<int64>("id", Description = "The snapshot id to inspect.")

    let directoryArgument =
        Argument<string>(
            "directory",
            Description = "Optionally scope changes to a mirror-relative path, an absolute source path, or a path relative to the current directory.",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = (fun _ -> ".")
        )

    let profileOption = HistorySession.profileOption "query"
    let configOption = HistorySession.configOption ()

    let filesOption =
        Option<bool>("--files", Description = "Show changed file paths and change kinds in the directory tree.")

    let depthOption =
        Option<string | null>(
            "--depth",
            Description = "Limit displayed directory levels below the selected directory (0 shows only that directory; default: unlimited)."
        )

    let command = Command("snapshot", "Show the directory changes recorded by one snapshot.")
    command.Arguments.Add idArgument
    command.Arguments.Add directoryArgument
    command.Options.Add profileOption
    command.Options.Add configOption
    command.Options.Add filesOption
    command.Options.Add depthOption

    let parseDepth (text: string option) : Result<int voption, string> =
        match text with
        | None -> Ok ValueNone
        | Some text ->
            match Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, depth -> Ok(ValueSome depth)
            | false, _ -> Error "--depth must be a non-negative integer."

    let handler (parseResult: ParseResult) (_: CancellationToken) : Task<Outcome> =
        task {
            let snapshotId = parseResult.GetValue idArgument
            let raw = parseResult.GetValue directoryArgument |> nonNull
            let includeFiles = parseResult.GetValue filesOption

            match parseDepth (HistorySession.textOf parseResult depthOption) with
            | Error message -> return ErrorReporting.usage env.ErrorConsole message
            | Ok depth ->
                match HistorySession.resolve env (HistorySession.textOf parseResult configOption) (HistorySession.textOf parseResult profileOption) with
                | Error error -> return HistorySession.fail env (ConfigError.render error)
                | Ok target ->
                    return
                        HistorySession.withManifest target (fun handle ->
                            let resolution = HistorySession.resolveDirectory env target handle raw

                            match ManifestQueries.snapshots handle |> List.tryFind (fun snapshot -> snapshot.SnapshotId = snapshotId) with
                            | None -> HistorySession.fail env (HistoryError.render (HistoryError.UnknownSnapshot(snapshotId, target.Name)))
                            | Some snapshot ->
                                let rollup =
                                    History.rollup resolution.Directory includeFiles (ManifestQueries.snapshotVersions handle snapshotId)

                                match rollup with
                                | [] ->
                                    HistorySession.show
                                        env
                                        [ Line.Text(Tone.Neutral, $"No changes recorded for snapshot #{snapshotId} in '{resolution.Directory}'.") ]

                                    Outcome.Success
                                | _ ->
                                    HistorySession.show env (HistoryView.ofRollup snapshot resolution.Directory target.SourceRoots depth rollup)
                                    Outcome.Success)
        }

    CommandAction.set env.ErrorConsole handler command
