/// `vara snapshot <id> [directory] [--files] [--depth <n>] [--interactive]`: what one snapshot changed, as a sparse directory tree
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

    let interactiveOption =
        Option<bool>(
            "--interactive",
            Description =
                "Browse the change tree with the keyboard: Up/Down move, Right/Left expand and collapse, Home/End jump, * expands all, q or Esc quits. Starts at --depth levels (default 1)."
        )

    let command = Command("snapshot", "Show the directory changes recorded by one snapshot.")
    command.Arguments.Add idArgument
    command.Arguments.Add directoryArgument
    command.Options.Add profileOption
    command.Options.Add configOption
    command.Options.Add filesOption
    command.Options.Add depthOption
    command.Options.Add interactiveOption

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
            let interactive = parseResult.GetValue interactiveOption

            match parseDepth (HistorySession.textOf parseResult depthOption) with
            | Error message -> return ErrorReporting.usage env.ErrorConsole message
            | Ok depth ->
                match HistorySession.resolve env (HistorySession.textOf parseResult configOption) (HistorySession.textOf parseResult profileOption) with
                | Error error -> return HistorySession.fail env (ConfigError.render error)
                | Ok target ->
                    // The manifest is read and closed first, so a browsing session holds nothing open.
                    let read =
                        HistorySession.withManifest target (fun handle ->
                            let resolution = HistorySession.resolveDirectory env target handle raw

                            match ManifestQueries.snapshots handle |> List.tryFind (fun snapshot -> snapshot.SnapshotId = snapshotId) with
                            | None -> Choice1Of2(HistorySession.fail env (HistoryError.render (HistoryError.UnknownSnapshot(snapshotId, target.Name))))
                            | Some snapshot ->
                                let rollup =
                                    History.rollup resolution.Directory includeFiles (ManifestQueries.snapshotVersions handle snapshotId)

                                match rollup with
                                | [] ->
                                    HistorySession.show
                                        env
                                        [ Line.Text(Tone.Neutral, $"No changes recorded for snapshot #{snapshotId} in '{resolution.Directory}'.") ]

                                    Choice1Of2 Outcome.Success
                                | _ -> Choice2Of2(snapshot, resolution.Directory, rollup))

                    match read with
                    | Choice1Of2 outcome -> return outcome
                    | Choice2Of2(snapshot, directory, rollup) ->
                        if interactive && HistorySession.canPrompt env then
                            let state =
                                TreeBrowser.create (HistoryView.snapshotHeader snapshot) directory target.SourceRoots depth rollup

                            return TreeSession.run env state
                        else
                            if interactive then
                                Terminal.writeLine env.ErrorConsole Terminal.neutralStyle "The interactive view is unavailable here; showing the static tree."

                            HistorySession.show env (HistoryView.ofRollup snapshot directory target.SourceRoots depth rollup)
                            return Outcome.Success
        }

    CommandAction.set env.ErrorConsole handler command
