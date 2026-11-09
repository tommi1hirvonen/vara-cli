/// `vara show <path> (--at <date> | --version <id>) [--force-binary]`: streams one version's content to standard
/// output, byte for byte, without writing any file.
module Vara.Cli.ShowCommand

#nowarn "3265"

open System
open System.CommandLine
open System.IO
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

/// Whether the first bytes of the stream look binary. The stream is left at its start.
let looksBinary (stream: Stream) : bool =
    let sample = Array.zeroCreate<byte> ContentSample.size

    // Reads until the sample is full or the stream ends.
    let rec fill (total: int) : int =
        if total = sample.Length then
            total
        else
            match stream.Read(sample, total, sample.Length - total) with
            | 0 -> total
            | read -> fill (total + read)

    let total = fill 0
    stream.Position <- 0L
    ContentSample.looksBinary sample total

/// The version the options name, in the history of the path.
let selectorOf (at: DateTimeOffset voption) (version: int64 voption) : VersionSelector =
    match version, at with
    | ValueSome versionId, _ -> VersionSelector.ById versionId
    | ValueNone, ValueSome time -> VersionSelector.AsOf time
    | ValueNone, ValueNone -> failwith "a version needs an --at or a --version, which the command checks first"

let create (env: CliEnv) : Command =
    let pathArgument =
        Argument<string>(
            "path",
            Description = "The file whose content to show - a mirror-relative path, an absolute source path, or a path relative to the current directory."
        )

    let profileOption = HistorySession.profileOption "query"
    let configOption = HistorySession.configOption ()

    let atOption =
        Option<string | null>("--at", Description = DateInput.help "Show the version current as of this date/time")

    let versionOption =
        Option<Nullable<int64>>("--version", Description = "Show this specific version id (see the 'history' command).")

    let forceBinaryOption =
        Option<bool>("--force-binary", Description = "Stream binary content to an interactive terminal instead of refusing it.")

    let command = Command("show", "Stream a specific version's content to standard output, without writing it to disk.")
    command.Arguments.Add pathArgument
    command.Options.Add profileOption
    command.Options.Add atOption
    command.Options.Add versionOption
    command.Options.Add configOption
    command.Options.Add forceBinaryOption

    let handler (parseResult: ParseResult) (_: CancellationToken) : Task<Outcome> =
        task {
            let raw = parseResult.GetValue pathArgument |> nonNull
            let atText = HistorySession.textOf parseResult atOption
            let version = parseResult.GetValue versionOption
            let forceBinary = parseResult.GetValue forceBinaryOption

            let refusal =
                HistorySession.exactlyOne env atText.IsSome version.HasValue "--at <date>" "--version <id>"

            match refusal with
            | ValueSome outcome -> return outcome
            | ValueNone ->
                match HistorySession.resolve env (HistorySession.textOf parseResult configOption) (HistorySession.textOf parseResult profileOption) with
                | Error error -> return HistorySession.fail env (ConfigError.render error)
                | Ok target ->
                    let at =
                        match atText with
                        | None -> Ok ValueNone
                        | Some text -> HistorySession.parseDate env "--at" text |> Result.map ValueSome

                    match at with
                    | Error message -> return HistorySession.fail env message
                    | Ok at ->
                        return
                            HistorySession.withManifest target (fun handle ->
                                let path = HistorySession.tryResolveFile env target handle raw |> ValueOption.defaultValue raw
                                let history = History.versionsOf (SqliteManifest.versionsOfPath handle) (MirrorPath.ofString path)
                                let versionId = if version.HasValue then ValueSome version.Value else ValueNone

                                let hash =
                                    History.resolveVersion path (selectorOf at versionId) history
                                    |> Result.bind (History.showable path)

                                match hash with
                                | Error error -> HistorySession.fail env (HistoryError.render error)
                                | Ok hash ->
                                    use content = ContentStore.openRead target.Layout hash
                                    // A redirected output has no terminal to protect, so binary content streams freely.
                                    let outputIsTerminal = env.Capabilities.IsTerminal

                                    if not (forceBinary || not outputIsTerminal) && looksBinary content then
                                        HistorySession.fail env (HistoryError.render (HistoryError.BinaryContent path))
                                    else
                                        use output = env.OpenStandardOutput()
                                        content.CopyTo output
                                        Outcome.Success)
        }

    CommandAction.set env.ErrorConsole handler command
