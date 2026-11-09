/// The stages every history command shares (design D1): resolve the profile, open the manifest read-only, resolve the
/// path the user typed, show a `Display`. A command composes them with its own projection and extends nothing.
module Vara.Cli.HistorySession

open System
open System.CommandLine
open Vara.Domain
open Vara.Engine

/// What a history command works on: the profile's name, where its mirror and store are, and the configured source
/// roots (in mirror form) so that a report can mark them. A target found from the working directory has no sources.
type Target =
    { Name: string
      Layout: StoreLayout
      SourceRoots: Set<string> }

let private targetOf (resolution: Resolution) : Target =
    match resolution with
    | Resolution.Configured profile ->
        { Name = Profile.name profile
          Layout = ContentStore.layout (AbsolutePath.value (Profile.target profile))
          SourceRoots =
            Profile.sources profile
            |> List.map (fun source -> SourceLocation.toMirror (AbsolutePath.value source.Path))
            |> Set.ofList }
    | Resolution.Discovered discovered ->
        { Name = discovered.Name
          Layout = ContentStore.layout (AbsolutePath.value discovered.Root)
          SourceRoots = Set.empty }

/// The profile of a command: named (and read from the configuration file), or found from the working directory.
let resolve (env: CliEnv) (configPath: string option) (profileName: string option) : Result<Target, ConfigError> =
    ProfileResolution.forBrowsing (ConfigFile.locate configPath) profileName env.CurrentDirectory
    |> Result.map targetOf

/// Runs the body with the manifest open read-only, then closes it. A target with no manifest is read as empty, and
/// nothing is created for it.
let withManifest (target: Target) (body: ManifestHandle -> 'a) : 'a =
    let handle = SqliteManifest.openReadOnly (SqliteManifest.pathIn target.Layout.MirrorRoot)

    try
        body handle
    finally
        SqliteManifest.close handle

let private context (env: CliEnv) (target: Target) : PathContext =
    { MirrorRoot = target.Layout.MirrorRoot
      WorkingDirectory = env.CurrentDirectory
      IsWithinMirror = ContentStore.isWithinMirror target.Layout }

/// The recorded path a file argument names, by the flexible forms; none when no reading matches a recorded path.
let tryResolveFile (env: CliEnv) (target: Target) (handle: ManifestHandle) (raw: string) : string voption =
    PathInput.resolve (context env target) (SqliteManifest.hasPath handle) raw

/// The recorded directory a path argument names, by the same flexible forms; none when it matches no directory.
let tryResolveTrackedDirectory (env: CliEnv) (target: Target) (handle: ManifestHandle) (raw: string) : string voption =
    PathInput.resolve (context env target) (fun candidate -> SqliteManifest.hasPrefix handle (Prefix.ofDirectory candidate)) raw

/// Whether something is recorded under the directory, live or deleted, at any time.
let isDirectoryTracked (handle: ManifestHandle) (directory: string) : bool =
    SqliteManifest.hasPrefix handle (Prefix.ofDirectory directory)

/// The directory a directory argument names. `.` or blank is the mirror root, or, from inside a source, that source
/// directory when it has history.
let resolveDirectory (env: CliEnv) (target: Target) (handle: ManifestHandle) (raw: string) : DirectoryResolution =
    PathInput.resolveDirectory (context env target) (isDirectoryTracked handle) raw

/// Writes a display to the console: in colour on a console that shows it, as plain text otherwise.
let show (env: CliEnv) (display: Display) : unit =
    match OutputMode.choose false env.Capabilities with
    | OutputMode.Live -> LiveRenderer.print env.Console display
    | OutputMode.Plain
    | OutputMode.Json -> PlainRenderer.write env.Console display

/// A report of a refusal or an error, on standard error in the hard-error style; the exit code is a hard error's.
let fail (env: CliEnv) (message: string) : Outcome = ErrorReporting.report env.ErrorConsole message

/// The text of a date/time option, as a moment, or the friendly error that names the option and the value.
let parseDate (env: CliEnv) (optionName: string) (text: string) : Result<DateTimeOffset, string> =
    DateInput.parse env.UtcOffsetAt optionName text |> Result.mapError DateInput.describe

/// Whether the session can ask a question: input is not redirected and the console shows a live display.
let canPrompt (env: CliEnv) : bool =
    env.Capabilities.InputIsInteractive
    && (OutputMode.choose false env.Capabilities = OutputMode.Live)

/// A refusal when two options that exclude each other are both given.
let mutuallyExclusive (env: CliEnv) (firstGiven: bool) (secondGiven: bool) (first: string) (second: string) : Outcome voption =
    if firstGiven && secondGiven then
        ValueSome(ErrorReporting.usage env.ErrorConsole $"{first} and {second} are mutually exclusive.")
    else
        ValueNone

/// A refusal unless exactly one of two options is given. The usages ("--at <date>") say what to pass.
let exactlyOne (env: CliEnv) (firstGiven: bool) (secondGiven: bool) (firstUsage: string) (secondUsage: string) : Outcome voption =
    if firstGiven && secondGiven then
        mutuallyExclusive env true true (firstUsage.Split(' ')[0]) (secondUsage.Split(' ')[0])
    elif firstGiven || secondGiven then
        ValueNone
    else
        ValueSome(ErrorReporting.usage env.ErrorConsole $"specify either {firstUsage} or {secondUsage}.")

/// The first refusal of a list of checks. Each check runs only when the ones before it passed, because a refusal
/// reports itself as it is made.
let firstRefusal (checks: (unit -> Outcome voption) list) : Outcome voption =
    checks |> List.tryPick (fun check -> check () |> ValueOption.toOption) |> ValueOption.ofOption

/// `--config`, as every command declares it.
let configOption () : Option<string | null> =
    Option<string | null>("--config", Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml).")

/// `--profile <name>`, as the commands that take it declare it.
let profileOption (purpose: string) : Option<string | null> =
    Option<string | null>(
        "--profile",
        Description = $"The profile to {purpose}. Optional when the current directory is inside a profile's target root."
    )

/// The option's text, if it was given.
let textOf (parseResult: ParseResult) (option: Option<string | null>) : string option =
    parseResult.GetValue option |> Option.ofObj
