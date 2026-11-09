/// Tests of what `vara backup` shows and writes, through the command with a test console. Each module is named after a
/// requirement of the owned specs and each test after one of its scenarios.
module Vara.Cli.Tests.PresentationCommandTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Spectre.Console.Testing
open Xunit
open Vara.Cli
open Vara.Cli.Tests.BackupCommandTests
open Vara.Domain
open Vara.Engine

let private live: Capabilities = { IsTerminal = true; SupportsColor = true; NoColorRequested = false; InputIsInteractive = true }

/// A console that draws live regions, on a terminal as far as the command can tell.
let private liveEnv (env: CliEnv) : CliEnv =
    let console = (new TestConsole()).EmitAnsiSequences().Interactive()
    console.Profile.Width <- 100
    { env with Console = console; Capabilities = live }

let private args (setup: Scratch) (extra: string list) : string array =
    Array.ofList ([ "backup"; "docs"; "--config"; setup.Config ] @ extra)

/// Culture-sensitive string asserts ignore control characters, so escapes are looked for as characters.
let private hasEscape (text: string) : bool = text.Contains(char 27)

let private parse (text: string) : JsonElement = JsonDocument.Parse(text).RootElement

module ``Machine-readable JSON output for the backup command`` =
    [<Fact>]
    let ``JSON output for a completed real run`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            write setup "b.txt" "beta"
            // A terminal that could draw: `--json` still shows nothing but the object.
            let result = invokeEnv liveEnv CancellationToken.None (args setup [ "--json" ])
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("", result.Output)
            let json = parse result.Json
            Assert.Equal("executed", json.GetProperty("mode").GetString())
            Assert.Equal("docs", json.GetProperty("profile").GetString())
            Assert.Equal(2, json.GetProperty("added").GetInt32())
            Assert.Equal(0, json.GetProperty("failed").GetInt32())
            Assert.Equal(9L, json.GetProperty("bytesTransferred").GetInt64())
            Assert.False(json.GetProperty("cancelled").GetBoolean())
            // Exactly one object: the text parses as a single document and holds one line.
            Assert.Single(result.Json.Split('\n', StringSplitOptions.RemoveEmptyEntries)) |> ignore)

    [<Fact>]
    let ``JSON output for a dry run`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let result = invokeEnv liveEnv CancellationToken.None (args setup [ "--dry-run"; "--json" ])
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("", result.Output)
            let json = parse result.Json
            Assert.Equal("dry-run", json.GetProperty("mode").GetString())
            Assert.Equal(1, json.GetProperty("added").GetInt32())
            Assert.Equal(5L, json.GetProperty("bytesTransferred").GetInt64())
            Assert.False(File.Exists(Path.Combine(setup.Target, ".vara", "profile.db"))))

    [<Fact>]
    let ``Non-JSON output unaffected`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let result = invoke (args setup [])
            Assert.Equal("", result.Json)
            Assert.Contains("Backup completed", result.Output)

            let dry = invoke (args setup [ "--dry-run" ])
            Assert.Equal("", dry.Json)
            Assert.Contains("Dry run - no changes were made", dry.Output))

    [<Fact>]
    let ``a cancelled run reports it and exits with the partial-failure code`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            use source = new CancellationTokenSource()
            source.Cancel()
            let result = invokeEnv id source.Token (args setup [ "--json" ])
            Assert.Equal(2, result.ExitCode)
            Assert.True((parse result.Json).GetProperty("cancelled").GetBoolean()))

module ``Unclassified exceptions are rendered in the hard-error style`` =
    let private crashing (env: CliEnv) : CliEnv =
        { env with
            MakeBackupEnv = fun stop -> { BackupRun.live stop with Clock = fun () -> raise (InvalidOperationException "boom") } }

    /// The escape sequences that open the hard-error style, as a console with colour writes them.
    let private errorOpening: string =
        let reference = (new TestConsole()).EmitAnsiSequences()
        Terminal.writeLine reference Terminal.errorStyle "x"
        reference.Output.Substring(0, reference.Output.IndexOf 'x')

    [<Fact>]
    let ``An unrecognized exception is raised`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"

            let withColour (env: CliEnv) : CliEnv =
                { crashing env with ErrorConsole = (new TestConsole()).EmitAnsiSequences() }

            let result = invokeEnv withColour CancellationToken.None (args setup [])
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("Unhandled exception: InvalidOperationException: boom", result.Errors)
            // With a stack trace, in the hard-error style, on standard error and not on standard output.
            Assert.Contains("   at ", result.Errors)
            Assert.Contains(errorOpening, result.Errors, StringComparison.Ordinal)
            Assert.DoesNotContain("Unhandled", result.Output))

    [<Fact>]
    let ``A recognized exception is still handled as before`` () =
        scratch (fun setup ->
            let target = (AbsolutePath.tryCreate setup.Target).Value
            use _held = RunLock.tryAcquire "other" target |> Result.defaultWith (fun _ -> failwith "no lock")
            let result = invoke (args setup [])
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("Another operation is already using target", result.Errors)
            Assert.DoesNotContain("   at ", result.Errors))

module ``Dry-run scan progress indication`` =
    [<Fact>]
    let ``Dry-run planning in a live terminal`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let result = invokeEnv liveEnv CancellationToken.None (args setup [ "--dry-run" ])
            Assert.Equal(0, result.ExitCode)
            let scanning = result.Output.IndexOf "Scanning files..."
            let summary = result.Output.IndexOf "Dry run - no changes were made"
            Assert.True(scanning >= 0, "the scan indicator was drawn")
            Assert.True(summary > scanning, "the summary follows the indicator"))

    [<Fact>]
    let ``Dry-run JSON output`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let result = invokeEnv liveEnv CancellationToken.None (args setup [ "--dry-run"; "--json" ])
            Assert.DoesNotContain("Scanning", result.Output)
            Assert.StartsWith("{", result.Json))

    [<Fact>]
    let ``Dry-run with redirected output`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let result = invoke (args setup [ "--dry-run" ])
            Assert.DoesNotContain("Scanning", result.Output)
            Assert.False(hasEscape result.Output)
            Assert.Contains("Dry run - no changes were made", result.Output))

module ``Automatic system sleep is prevented during a real backup`` =
    let private powered (requests: int ref) (env: CliEnv) : CliEnv = { env with Power = countingPower requests }

    [<Fact>]
    let ``Successful real backup`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let requests = ref 0
            let result = invokeEnv (powered requests) CancellationToken.None (args setup [])
            Assert.Equal(0, result.ExitCode)
            Assert.Equal(1, requests.Value))

    [<Fact>]
    let ``Dry run`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let requests = ref 0
            let result = invokeEnv (powered requests) CancellationToken.None (args setup [ "--dry-run" ])
            Assert.Equal(0, result.ExitCode)
            Assert.Equal(0, requests.Value))

module ``Sleep-prevention request failure does not block backup`` =
    [<Fact>]
    let ``Windows refuses request activation`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"

            let refusing (env: CliEnv) : CliEnv =
                { env with Power.Create = fun _ -> Error 5 }

            let result = invokeEnv refusing CancellationToken.None (args setup [])
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Warning: Windows sleep prevention could not be activated", result.Errors)
            Assert.Contains("Backup completed", result.Output)
            Assert.True(File.Exists(Path.Combine(setup.Target, ".vara", "profile.db"))))

module ``Non-interactive or color-incapable output falls back to plain text`` =
    [<Fact>]
    let ``Output redirected to a file or pipe`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let result = invoke (args setup [])
            Assert.False(hasEscape result.Output)
            // Progress is appended as lines, never redrawn in place.
            Assert.Contains("5 B / 5 B (100.0%) - Files 1 / 1", result.Output))

    [<Fact>]
    let ``NO_COLOR is set`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let noColour (env: CliEnv) : CliEnv = { liveEnv env with Capabilities = { live with NoColorRequested = true } }
            let result = invokeEnv noColour CancellationToken.None (args setup [])
            Assert.False(hasEscape result.Output)
            Assert.DoesNotContain("Scanning files", result.Output)
            Assert.Contains("Backup completed", result.Output))

    [<Fact>]
    let ``Terminal does not support color`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let noColour (env: CliEnv) : CliEnv = { liveEnv env with Capabilities = { live with SupportsColor = false } }
            let result = invokeEnv noColour CancellationToken.None (args setup [])
            Assert.False(hasEscape result.Output)
            Assert.Contains("Backup completed", result.Output))

module ``Outcome severity is visually distinct`` =
    [<Fact>]
    let ``Outcome report includes multi-line supporting detail`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            let result = invokeEnv liveEnv CancellationToken.None (args setup [])
            let lines = result.Output.Split('\n')
            let headline = lines |> Array.find (fun line -> line.Contains "Backup completed")
            let detail = lines |> Array.find (fun line -> line.Contains "Added:       1")
            Assert.True(hasEscape headline)
            Assert.False(hasEscape detail))
