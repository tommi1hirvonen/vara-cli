/// `vara snapshot <id> [directory] [--files] [--depth <n>] [--interactive]`: what one snapshot changed, as a sparse directory tree
/// with each directory's counts rolled up by change kind.
module Vara.Cli.SnapshotCommand

#nowarn "3265"

open System
open System.CommandLine
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

/// What reading the snapshot leaves to do: nothing, because the outcome is known, or a tree to show.
[<RequireQualifiedAccess>]
type private ReadOutcome =
    | Finished of Outcome
    | Changes of snapshot: SnapshotInfo * directory: string * rollup: RollupDirectory list * viewing: ChangeTree.Viewing

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
                "Browse the change tree with the keyboard: " + ChangeTree.interactiveHelp
        )

    let command = Command("snapshot", "Show the directory changes recorded by one snapshot.")
    command.Arguments.Add idArgument
    command.Arguments.Add directoryArgument
    command.Options.Add profileOption
    command.Options.Add configOption
    command.Options.Add filesOption
    command.Options.Add depthOption
    command.Options.Add interactiveOption

    let handler (parseResult: ParseResult) (_: CancellationToken) : Task<Outcome> =
        task {
            let snapshotId = parseResult.GetValue idArgument
            let raw = parseResult.GetValue directoryArgument |> nonNull
            let includeFiles = parseResult.GetValue filesOption
            let interactive = parseResult.GetValue interactiveOption

            match ChangeTree.parseDepth (HistorySession.textOf parseResult depthOption) with
            | Error message -> return ErrorReporting.usage env.ErrorConsole message
            | Ok depth ->
                match HistorySession.resolve env (HistorySession.textOf parseResult configOption) (HistorySession.textOf parseResult profileOption) with
                | Error error -> return HistorySession.fail env (ConfigError.render error)
                | Ok target ->
                    // The manifest is read and closed first, so a browsing session holds nothing open.
                    let treeMode = ChangeTree.modeOf env interactive true

                    let read =
                        HistorySession.withManifest target (fun handle ->
                            let resolution = HistorySession.resolveDirectory env target handle raw

                            match ManifestQueries.snapshots handle |> List.tryFind (fun snapshot -> snapshot.SnapshotId = snapshotId) with
                            | None -> ReadOutcome.Finished(HistorySession.fail env (HistoryError.render (HistoryError.UnknownSnapshot(snapshotId, target.Name))))
                            | Some snapshot ->
                                let rollup, viewing =
                                    ChangeTree.loadChanges handle snapshotId resolution.Directory includeFiles treeMode

                                match rollup with
                                | [] ->
                                    HistorySession.show
                                        env
                                        [ Line.Text(Tone.Neutral, $"No changes recorded for snapshot #{snapshotId} in '{resolution.Directory}'.") ]

                                    ReadOutcome.Finished Outcome.Success
                                | _ -> ReadOutcome.Changes(snapshot, resolution.Directory, rollup, viewing))

                    match read with
                    | ReadOutcome.Finished outcome -> return outcome
                    | ReadOutcome.Changes(snapshot, directory, rollup, viewing) ->
                        let shown =
                            ChangeTree.present
                                env
                                { StaticHeader = HistoryView.snapshotHeader snapshot
                                  InteractiveHeader = HistoryView.snapshotHeader snapshot
                                  Scope = directory
                                  SourceRoots = target.SourceRoots
                                  Depth = depth
                                  Rollup = rollup
                                  Viewing = viewing }

                        return shown |> ValueOption.defaultValue Outcome.Success
        }

    CommandAction.set env.ErrorConsole handler command
