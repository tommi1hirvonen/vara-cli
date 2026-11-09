/// Tests of `vara backup`, named after the scenarios of openspec/specs/backup-execution/spec.md that this change owns
/// through the command, in modules named after their requirements.
module Vara.Cli.Tests.BackupCommandTests

open System
open System.CommandLine
open System.Diagnostics
open System.IO
open System.Threading
open Spectre.Console.Testing
open Xunit
open Vara.Cli
open Vara.Domain
open Vara.Engine

type Scratch =
    { Root: string
      Source: string
      Target: string
      Config: string }

let scratch (body: Scratch -> unit) =
    let root = Path.Combine(Path.GetTempPath(), $"vara-cli-test-{Guid.NewGuid():N}")
    let source = Path.Combine(root, "source")
    let target = Path.Combine(root, "target")
    Directory.CreateDirectory source |> ignore
    let config = Path.Combine(root, "profiles.yml")

    File.WriteAllText(
        config,
        $"profiles:\n  - name: docs\n    target: {target}\n    sources:\n      - path: {source}\n"
    )

    try
        body
            { Root = root
              Source = source
              Target = target
              Config = config }
    finally
        for file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) |> Seq.toArray do
            File.SetAttributes(file, FileAttributes.Normal)

        Directory.Delete(root, true)

let write (setup: Scratch) (name: string) (text: string) : unit =
    let path = Path.Combine(setup.Source, name)
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    File.WriteAllText(path, text)

type Invocation =
    { ExitCode: int
      Output: string
      Errors: string
      /// What `--json` wrote to its own writer.
      Json: string
      Syntax: string }

/// Prompts for a test that expects no question: asking one is a failure.
let noPrompts: Prompts =
    { Select = fun title _ _ -> failwith $"unexpected selection prompt: {title}"
      Confirm = fun question -> failwith $"unexpected confirmation prompt: {question}" }

/// Power calls that succeed and count their requests, so that no test keeps the machine awake.
let countingPower (requests: int ref) : PowerCalls =
    { Create =
        fun _ ->
            requests.Value <- requests.Value + 1
            Ok 1n
      SetSystemRequired = fun _ -> Ok()
      ClearSystemRequired = fun _ -> Ok()
      Close = fun _ -> Ok() }

/// Runs the command with the console and the other effects as the test says. The default console is not a terminal,
/// so the output is plain.
let invokeEnv (customize: CliEnv -> CliEnv) (token: CancellationToken) (args: string array) : Invocation =
    let console = new TestConsole()
    let errorConsole = new TestConsole()
    // Wide enough that a message is never wrapped across lines.
    console.Profile.Width <- 400
    errorConsole.Profile.Width <- 400
    let syntax = new StringWriter()
    let output = new StringWriter()
    let json = new StringWriter()

    let env =
        customize
            { Console = console
              Capabilities = Terminal.capabilitiesOf console
              ErrorConsole = errorConsole
              JsonOut = json
              Prompts = noPrompts
              CurrentDirectory = Environment.CurrentDirectory
              UtcOffsetAt = fun _ -> TimeSpan.Zero
              OpenStandardOutput = fun () -> Stream.Null
              MakeBackupEnv = BackupRun.live
              Power = countingPower (ref 0)
              TerminalSession = null }

    let root = Composition.rootCommand () |> Composition.withCommands env

    let configuration =
        InvocationConfiguration(Output = output, Error = syntax, ProcessTerminationTimeout = Nullable())

    let exitCode = root.Parse(args).InvokeAsync(configuration, token).GetAwaiter().GetResult()

    { ExitCode = exitCode
      Output = (env.Console :?> TestConsole).Output
      Errors = (env.ErrorConsole :?> TestConsole).Output
      Json = json.ToString()
      Syntax = syntax.ToString() + output.ToString() }

let invokeWith (token: CancellationToken) (args: string array) : Invocation = invokeEnv id token args

let invoke (args: string array) : Invocation = invokeWith CancellationToken.None args

let private mirrorFile (setup: Scratch) (name: string) : string =
    let key = ScanRules.toMirrorPath (Path.Combine(setup.Source, name)) |> ValueOption.get |> MirrorPath.value
    Path.Combine(setup.Target, key)

module ``Backup command accepts profile as a positional argument`` =
    [<Fact>]
    let ``Running backup with positional profile argument`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let result = invoke [| "backup"; "docs"; "--config"; setup.Config |]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Backup completed", result.Output)
            Assert.Contains("Added:       1", result.Output)
            Assert.Equal("alpha", File.ReadAllText(mirrorFile setup "a.txt")))

    [<Fact>]
    let ``Running backup without a positional profile argument`` () =
        scratch (fun setup ->
            let result = invoke [| "backup"; "--config"; setup.Config |]
            Assert.NotEqual(0, result.ExitCode)
            Assert.Contains("profile", result.Syntax)
            // Nothing ran.
            Assert.False(Directory.Exists setup.Target))

    [<Fact>]
    let ``Running backup with legacy --profile option`` () =
        scratch (fun setup ->
            let result = invoke [| "backup"; "--profile"; "docs"; "--config"; setup.Config |]
            Assert.NotEqual(0, result.ExitCode)
            // The option does not exist, so the parser rejects the invocation as a syntax error before anything runs.
            Assert.Contains("Unrecognized command or argument", result.Syntax)
            Assert.False(Directory.Exists setup.Target))

module ``Outcome severity determines process exit code`` =
    [<Fact>]
    let ``A clean run exits successfully`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            Assert.Equal(0, (invoke [| "backup"; "docs"; "--config"; setup.Config |]).ExitCode)
            // An incremental run with nothing to do is clean too.
            Assert.Equal(0, (invoke [| "backup"; "docs"; "--config"; setup.Config |]).ExitCode))

    [<Fact>]
    let ``A run with failed paths exits with the partial-failure code`` () =
        scratch (fun setup ->
            write setup "locked.txt" "locked"
            write setup "fine.txt" "fine"
            use hold = new FileStream(Path.Combine(setup.Source, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None)
            let result = invoke [| "backup"; "docs"; "--config"; setup.Config |]
            Assert.Equal(2, result.ExitCode)
            Assert.Contains("Failed (1)", result.Output)
            Assert.Contains("locked.txt", result.Output))

    [<Fact>]
    let ``A refused run exits with the hard-error code and names the busy target`` () =
        scratch (fun setup ->
            let target = (AbsolutePath.tryCreate setup.Target).Value
            use _held = (RunLock.tryAcquire "other" target |> Result.defaultWith (fun _ -> failwith "no lock"))
            let result = invoke [| "backup"; "docs"; "--config"; setup.Config |]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("Another operation is already using target", result.Errors)
            Assert.DoesNotContain("Backup completed", result.Output))

module ``Graceful cancellation via CtrlC`` =
    [<Fact>]
    let ``Second Ctrl+C forces immediate termination`` () =
        let forced = ref None
        let cancellation = GracefulCancellation(fun code -> forced.Value <- Some code)
        // The first request is cooperative; the second exits at once with the hard-error code.
        Assert.True(cancellation.RequestCancellation())
        Assert.Equal(None, forced.Value)
        Assert.False(cancellation.RequestCancellation())
        Assert.Equal(Some(Outcome.exitCode Outcome.HardError), forced.Value)

    [<Fact>]
    let ``A stop requested before the run started cancels it with the partial-failure code`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            use source = new CancellationTokenSource()
            source.Cancel()
            let result = invokeWith source.Token [| "backup"; "docs"; "--config"; setup.Config |]
            Assert.Equal(2, result.ExitCode)
            Assert.Contains("Backup cancelled", result.Output)
            Assert.False(File.Exists(mirrorFile setup "a.txt")))

module ``Dry-run mode reports planned changes without executing them`` =
    [<Fact>]
    let ``Dry run reports planned changes without writing anything`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let result = invoke [| "backup"; "docs"; "--config"; setup.Config; "--dry-run" |]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Dry run - no changes were made", result.Output)
            Assert.Contains("Added:       1", result.Output)
            Assert.Contains("To transfer: 5 B", result.Output)
            // No mirror, no store, no manifest.
            Assert.False(File.Exists(mirrorFile setup "a.txt"))
            Assert.False(File.Exists(Path.Combine(setup.Target, ".vara", "profile.db"))))

    [<Fact>]
    let ``Dry run refuses to start while a real run is in progress for the same target`` () =
        scratch (fun setup ->
            let target = (AbsolutePath.tryCreate setup.Target).Value
            use _held = (RunLock.tryAcquire "other" target |> Result.defaultWith (fun _ -> failwith "no lock"))
            let result = invoke [| "backup"; "docs"; "--config"; setup.Config; "--dry-run" |]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("Another operation is already using target", result.Errors))

    [<Fact>]
    let ``Dry run reports unreadable paths without treating them as an error`` () =
        scratch (fun setup ->
            write setup "keep.txt" "k"
            write setup "denied-dir\\hidden.txt" "h"
            let denied = Path.Combine(setup.Source, "denied-dir")

            let run (arguments: string) =
                let info = ProcessStartInfo("cmd.exe", $"/c icacls \"{denied}\" {arguments}")
                info.UseShellExecute <- false
                info.RedirectStandardOutput <- true
                use proc = Process.Start info |> nonNull
                proc.WaitForExit()

            run "/deny \"%USERNAME%:(RD)\""

            try
                let result = invoke [| "backup"; "docs"; "--config"; setup.Config; "--dry-run" |]
                // The unreadable path is reported as one a real run would skip; the dry run itself did not fail.
                Assert.Contains("denied-dir", result.Output)
                Assert.DoesNotContain("Unhandled", result.Errors)
                Assert.Equal(2, result.ExitCode)
            finally
                run "/remove:d \"%USERNAME%\"")

    [<Fact>]
    let ``No changes pending`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            invoke [| "backup"; "docs"; "--config"; setup.Config |] |> ignore
            let result = invoke [| "backup"; "docs"; "--config"; setup.Config; "--dry-run" |]
            Assert.Equal(0, result.ExitCode)

            for label in [ "Added:       0"; "Changed:     0"; "Moved:       0"; "Deleted:     0"; "To transfer: 0 B" ] do
                Assert.Contains(label, result.Output))

module ``Profile configuration errors are reported`` =
    [<Fact>]
    let ``Configuration missing`` () =
        scratch (fun setup ->
            let missing = Path.Combine(setup.Root, "nowhere.yml")
            let result = invoke [| "backup"; "docs"; "--config"; missing |]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains($"Profile configuration file not found at '{missing}'", result.Errors))

    [<Fact>]
    let ``Unknown profile`` () =
        scratch (fun setup ->
            let result = invoke [| "backup"; "nope"; "--config"; setup.Config |]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("No profile named 'nope' was found", result.Errors))

    [<Fact>]
    let ``Invalid profile`` () =
        scratch (fun setup ->
            File.WriteAllText(setup.Config, "profiles:\n  - name: docs\n    target: relative\\path\n    sources:\n      - path: also\\relative\n")
            let result = invoke [| "backup"; "docs"; "--config"; setup.Config |]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("Profile 'docs' is invalid", result.Errors))
