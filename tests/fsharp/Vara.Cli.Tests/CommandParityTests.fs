/// Runs the same fixtures through the published-shape C# and F# `vara backup` commands, as processes, and compares
/// what a script can see: the JSON object, the exit code and the counts of the plain-text summary.
module Vara.Cli.Tests.CommandParityTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open Vara.Cli.Tests.BackupCommandTests

/// The directory above the test binaries that holds the repository's `src` folder.
let private repositoryRoot: string =
    let rec up (directory: DirectoryInfo) : string =
        if Directory.Exists(Path.Combine(directory.FullName, "src", "Vara.Cli")) then directory.FullName
        else up (directory.Parent |> nonNull)

    up (DirectoryInfo AppContext.BaseDirectory)

/// "Debug" or "Release": the configuration this test assembly was built in, which the C# project is built in too.
let private configuration: string =
    DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent |> nonNull |> fun parent -> parent.Name

let private csharpDll = Path.Combine(repositoryRoot, "src", "Vara.Cli", "bin", configuration, "net11.0", "vara.dll")
let private fsharpDll = Path.Combine(AppContext.BaseDirectory, "vara.dll")

type private Run =
    { ExitCode: int
      Out: string
      Err: string }

let private start (dll: string) (arguments: string list) : Run =
    let info = ProcessStartInfo("dotnet")
    info.ArgumentList.Add dll

    for argument in arguments do
        info.ArgumentList.Add argument

    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use proc = Process.Start info |> nonNull
    let err = proc.StandardError.ReadToEndAsync()
    let out = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit()
    { ExitCode = proc.ExitCode; Out = out; Err = err.GetAwaiter().GetResult() }

/// The JSON object without the members that differ between two runs.
let private normalized (json: string) : string =
    let node = JsonNode.Parse(json) |> nonNull
    let obj = node.AsObject()
    obj.Remove "startedAt" |> ignore
    obj.Remove "completedAt" |> ignore
    obj.ToJsonString()

/// The count lines of a plain-text summary, which are the same words in both builds.
let private counts (text: string) : string list =
    Regex.Matches(text, @"^  (Added|Changed|Moved|Deleted|To transfer|Transferred):\s+(.+?)\s*$", RegexOptions.Multiline)
    |> Seq.map (fun found -> $"{found.Groups[1].Value}={found.Groups[2].Value}")
    |> List.ofSeq

/// Runs `arguments` for a profile whose target is a fresh directory, once per build, over the same source.
let private both (setup: Scratch) (arguments: string list) : Run * Run =
    let configFor (name: string) : string =
        let target = Path.Combine(setup.Root, $"target-{name}")
        let config = Path.Combine(setup.Root, $"profiles-{name}.yml")

        File.WriteAllText(
            config,
            $"profiles:\n  - name: docs\n    target: {target}\n    sources:\n      - path: {setup.Source}\n"
        )

        config

    let run (dll: string) (name: string) = start dll ([ "backup"; "docs"; "--config"; configFor name ] @ arguments)
    run csharpDll "cs", run fsharpDll "fs"

let private same (cs: Run) (fs: Run) : unit =
    Assert.Equal(cs.ExitCode, fs.ExitCode)
    Assert.Equal(normalized cs.Out, normalized fs.Out)

module ``Backup command parity`` =
    [<Fact>]
    let ``the built commands exist`` () =
        Assert.True(File.Exists csharpDll, $"build the C# CLI first: {csharpDll}")
        Assert.True(File.Exists fsharpDll)

    [<Fact>]
    let ``a real run gives the same JSON, exit code and counts`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            write setup "sub/b.txt" "beta beta"
            let cs, fs = both setup [ "--json" ]
            same cs fs
            Assert.Equal(0, fs.ExitCode)

            let csText, fsText = both setup []
            Assert.Equal<string list>(counts csText.Out, counts fsText.Out))

    [<Fact>]
    let ``a dry run gives the same JSON, exit code and counts`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            write setup "sub/b.txt" "beta beta"
            let cs, fs = both setup [ "--dry-run"; "--json" ]
            same cs fs
            let mode = (JsonNode.Parse(fs.Out) |> nonNull).AsObject()["mode"] |> nonNull
            Assert.Equal("dry-run", mode.GetValue<string>())

            let csText, fsText = both setup [ "--dry-run" ]
            Assert.Equal<string list>(counts csText.Out, counts fsText.Out))

    [<Fact>]
    let ``a run with a failed path gives the same JSON, exit code and counts`` () =
        scratch (fun setup ->
            write setup "locked.txt" "locked"
            write setup "fine.txt" "fine"
            use _hold = new FileStream(Path.Combine(setup.Source, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None)
            let cs, fs = both setup [ "--json" ]
            same cs fs
            Assert.Equal(2, fs.ExitCode)

            let csText, fsText = both setup []
            Assert.Equal(csText.ExitCode, fsText.ExitCode)
            Assert.Equal<string list>(counts csText.Out, counts fsText.Out))

    [<Fact>]
    let ``a refused run gives the same exit code and names the busy target`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"

            let hold (name: string) =
                let target = Vara.Domain.AbsolutePath.tryCreate (Path.Combine(setup.Root, $"target-{name}")) |> Option.get
                Vara.Engine.RunLock.tryAcquire "other" target |> Result.defaultWith (fun _ -> failwith "no lock")

            use _csLock = hold "cs"
            use _fsLock = hold "fs"
            let cs, fs = both setup [ "--json" ]
            Assert.Equal(1, cs.ExitCode)
            Assert.Equal(cs.ExitCode, fs.ExitCode)
            Assert.Contains("Another operation is already using target", cs.Err)
            Assert.Contains("Another operation is already using target", fs.Err))
