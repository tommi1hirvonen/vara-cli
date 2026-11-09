/// `vara diff <path> (--left-at <date> | --left-version <id>) (--right-at <date> | --right-version <id>)`: the text
/// difference between two versions of a file, refused before any content is read when a side is too large or a link,
/// and before it is diffed when a side is binary.
module Vara.Cli.DiffCommand

#nowarn "3265"

open System
open System.CommandLine
open System.IO
open System.Threading
open System.Threading.Tasks
open DiffPlex
open DiffPlex.DiffBuilder
open DiffPlex.DiffBuilder.Model
open Vara.Domain
open Vara.Engine

/// The lines of a text diff, each marked as added, removed or unchanged.
let private diffLines (left: string) (right: string) : (DiffLineKind * string) list =
    InlineDiffBuilder(Differ()).BuildDiffModel(left, right).Lines
    |> Seq.map (fun line ->
        let kind =
            match line.Type with
            | ChangeType.Inserted -> DiffLineKind.Inserted
            | ChangeType.Deleted -> DiffLineKind.Deleted
            // Unchanged, and the kinds an inline diff does not produce, read as context. A BCL enum is open.
            | _ -> DiffLineKind.Unchanged

        kind, line.Text)
    |> List.ofSeq

let private readAll (stream: Stream) : string =
    use reader = new StreamReader(stream, leaveOpen = true)
    reader.ReadToEnd()

let create (env: CliEnv) : Command =
    let pathArgument =
        Argument<string>(
            "path",
            Description = "The file whose versions to diff - a mirror-relative path, an absolute source path, or a path relative to the current directory."
        )

    let profileOption = HistorySession.profileOption "query"
    let configOption = HistorySession.configOption ()

    let leftAtOption =
        Option<string | null>("--left-at", Description = DateInput.help "The earlier side of the diff: the version current as of this date/time")

    let leftVersionOption =
        Option<Nullable<int64>>("--left-version", Description = "The earlier side of the diff: this specific version id.")

    let rightAtOption =
        Option<string | null>("--right-at", Description = DateInput.help "The later side of the diff: the version current as of this date/time")

    let rightVersionOption =
        Option<Nullable<int64>>("--right-version", Description = "The later side of the diff: this specific version id.")

    let command = Command("diff", "Show a textual diff between two versions of the same file.")
    command.Arguments.Add pathArgument
    command.Options.Add profileOption
    command.Options.Add leftAtOption
    command.Options.Add leftVersionOption
    command.Options.Add rightAtOption
    command.Options.Add rightVersionOption
    command.Options.Add configOption

    let handler (parseResult: ParseResult) (_: CancellationToken) : Task<Outcome> =
        task {
            let raw = parseResult.GetValue pathArgument |> nonNull
            let leftAt = HistorySession.textOf parseResult leftAtOption
            let rightAt = HistorySession.textOf parseResult rightAtOption
            let leftVersion = parseResult.GetValue leftVersionOption
            let rightVersion = parseResult.GetValue rightVersionOption

            let refusal =
                HistorySession.firstRefusal
                    [ fun () -> HistorySession.exactlyOne env leftAt.IsSome leftVersion.HasValue "--left-at <date>" "--left-version <id>"
                      fun () -> HistorySession.exactlyOne env rightAt.IsSome rightVersion.HasValue "--right-at <date>" "--right-version <id>" ]

            match refusal with
            | ValueSome outcome -> return outcome
            | ValueNone ->
                match HistorySession.resolve env (HistorySession.textOf parseResult configOption) (HistorySession.textOf parseResult profileOption) with
                | Error error -> return HistorySession.fail env (ConfigError.render error)
                | Ok target ->
                    let parse (optionName: string) (text: string option) : Result<DateTimeOffset voption, string> =
                        match text with
                        | None -> Ok ValueNone
                        | Some text -> HistorySession.parseDate env optionName text |> Result.map ValueSome

                    match parse "--left-at" leftAt, parse "--right-at" rightAt with
                    | Error message, _
                    | _, Error message -> return HistorySession.fail env message
                    | Ok leftTime, Ok rightTime ->
                        return
                            HistorySession.withManifest target (fun handle ->
                                let path = HistorySession.tryResolveFile env target handle raw |> ValueOption.defaultValue raw
                                let history = History.versionsOf (SqliteManifest.versionsOfPath handle) (MirrorPath.ofString path)
                                let idOf (version: Nullable<int64>) = if version.HasValue then ValueSome version.Value else ValueNone

                                let versions =
                                    History.resolveVersion path (ShowCommand.selectorOf leftTime (idOf leftVersion)) history
                                    |> Result.bind (fun left ->
                                        History.resolveVersion path (ShowCommand.selectorOf rightTime (idOf rightVersion)) history
                                        |> Result.map (fun right -> left, right))

                                // Whether a side within the size limit is binary: a bounded sample of its first bytes.
                                let sampledBinary (oversized: int64 voption) (version: FileVersion) : bool =
                                    match oversized, History.contentHash version with
                                    | ValueNone, ValueSome hash ->
                                        use content = ContentStore.openRead target.Layout hash
                                        ShowCommand.looksBinary content
                                    | _ -> false

                                let sides =
                                    match versions with
                                    | Error error -> Error error
                                    | Ok(left, right) ->
                                        match History.diffable path left right with
                                        | Error(HistoryError.DiffTooLarge(_, limit, leftSize, rightSize)) ->
                                            // The side that is not too large may be binary too: each side's own problem is named.
                                            let leftBinary, rightBinary = sampledBinary leftSize left, sampledBinary rightSize right

                                            if leftBinary || rightBinary then
                                                Error(HistoryError.DiffTooLargeAndBinary(path, limit, leftSize, rightSize, leftBinary, rightBinary))
                                            else
                                                Error(HistoryError.DiffTooLarge(path, limit, leftSize, rightSize))
                                        | other -> other

                                match sides with
                                | Error error -> HistorySession.fail env (HistoryError.render error)
                                | Ok struct (leftHash, rightHash) ->
                                    use leftContent = ContentStore.openRead target.Layout leftHash
                                    use rightContent = ContentStore.openRead target.Layout rightHash
                                    let leftBinary = ShowCommand.looksBinary leftContent
                                    let rightBinary = ShowCommand.looksBinary rightContent

                                    if leftBinary || rightBinary then
                                        HistorySession.fail env (HistoryError.render (HistoryError.DiffBinary(path, leftBinary, rightBinary)))
                                    else
                                        let lines = diffLines (readAll leftContent) (readAll rightContent)
                                        HistorySession.show env (HistoryView.ofDiff lines)
                                        Outcome.Success)
        }

    CommandAction.set env.ErrorConsole handler command
