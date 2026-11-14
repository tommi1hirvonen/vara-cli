/// The command line as a script sees it: the built `vara.dll` run as a separate process, observed through its exit code,
/// standard output and standard error. The in-process command tests cover what each command does; these cover the
/// contract at the process boundary (exit codes 0, 1 and 2, a `--json` document alone on standard output, refusals on
/// standard error, and `show` streaming bytes unchanged).
module Vara.Cli.Tests.ProcessTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Xunit
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests

type private Run =
    { ExitCode: int
      Out: byte array
      Err: string }

    member run.Text = Text.Encoding.UTF8.GetString run.Out

let private varaDll: string = Path.Combine(AppContext.BaseDirectory, "vara.dll")

/// Runs `vara` with the arguments. Standard output is kept as bytes, so that `show` can be compared byte for byte.
let private vara (arguments: string list) : Run =
    let info = ProcessStartInfo "dotnet"
    info.ArgumentList.Add varaDll

    for argument in arguments do
        info.ArgumentList.Add argument

    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    // The child writes UTF-8 (`Program.main` sets it); left unset, standard error would be decoded with this host's
    // console code page.
    info.StandardErrorEncoding <- Text.UTF8Encoding false
    info.UseShellExecute <- false
    use proc = Process.Start info |> nonNull
    let err = proc.StandardError.ReadToEndAsync()
    use out = new MemoryStream()
    proc.StandardOutput.BaseStream.CopyTo out
    proc.WaitForExit()

    { ExitCode = proc.ExitCode
      Out = out.ToArray()
      Err = err.GetAwaiter().GetResult() }

let private backup (setup: Scratch) (arguments: string list) : Run =
    vara ([ "backup"; "docs"; "--config"; setup.Config ] @ arguments)

/// The whole of standard output parsed as one JSON object, which fails when anything else was written there. The times
/// are checked for presence and then taken out, since they differ between runs.
let private jsonDocument (run: Run) : JsonObject =
    let document = (JsonNode.Parse run.Text |> nonNull).AsObject()
    Assert.True(document.ContainsKey "startedAt")
    Assert.True(document.ContainsKey "completedAt")
    document.Remove "startedAt" |> ignore
    document.Remove "completedAt" |> ignore
    document

[<Fact>]
let ``the built command exists`` () = Assert.True(File.Exists varaDll)

[<Fact>]
let ``a backup with --json writes one JSON document to standard output and exits 0`` () =
    scratch (fun setup ->
        write setup "a.txt" "alpha"
        write setup "sub/b.txt" "beta beta"
        let run = backup setup [ "--json" ]

        Assert.Equal(0, run.ExitCode)

        Assert.Equal(
            """{"mode":"executed","profile":"docs","cancelled":false,"added":2,"changed":0,"moved":0,"deleted":0,"failed":0,"failedPaths":[],"bytesTransferred":14}""",
            (jsonDocument run).ToJsonString()
        ))

[<Fact>]
let ``a dry run with --json reports the plan and copies nothing into the mirror`` () =
    scratch (fun setup ->
        write setup "a.txt" "alpha"
        write setup "sub/b.txt" "beta beta"
        let run = backup setup [ "--dry-run"; "--json" ]

        Assert.Equal(0, run.ExitCode)

        Assert.Equal(
            """{"mode":"dry-run","profile":"docs","cancelled":false,"added":2,"changed":0,"moved":0,"deleted":0,"failed":0,"failedPaths":[],"bytesTransferred":14}""",
            (jsonDocument run).ToJsonString()
        )

        let mirrored =
            if Directory.Exists setup.Target then
                Directory.EnumerateFiles(setup.Target, "a.txt", SearchOption.AllDirectories) |> List.ofSeq
            else
                []

        Assert.Empty mirrored)

[<Fact>]
let ``an unreadable file makes the backup a partial failure with exit code 2`` () =
    scratch (fun setup ->
        write setup "locked.txt" "locked"
        write setup "fine.txt" "fine"
        use _hold = new FileStream(sourceFile setup "locked.txt", FileMode.Open, FileAccess.Read, FileShare.None)
        let run = backup setup [ "--json" ]

        Assert.Equal(2, run.ExitCode)
        let document = jsonDocument run
        Assert.Equal(1, document["added"] |> nonNull |> _.GetValue<int>())
        Assert.Equal(1, document["failed"] |> nonNull |> _.GetValue<int>())
        let failedPaths = (document["failedPaths"] |> nonNull).AsArray() |> Seq.map (fun path -> (path |> nonNull).GetValue<string>()) |> List.ofSeq
        Assert.Equal(1, failedPaths.Length)
        Assert.EndsWith(@"\source\locked.txt", failedPaths.Head))

[<Fact>]
let ``a backup refused at the start exits 1 and says why on standard error`` () =
    scratch (fun setup ->
        write setup "a.txt" "alpha"
        let target = Vara.Domain.AbsolutePath.tryCreate setup.Target |> Option.get
        use _lock = Vara.Engine.RunLock.tryAcquire "other" target |> Result.defaultWith (fun _ -> failwith "no lock")
        let run = backup setup [ "--json" ]

        Assert.Equal(1, run.ExitCode)
        Assert.Contains("Another operation is already using target", run.Err))

[<Fact>]
let ``an unknown profile exits 1 and is named on standard error`` () =
    scratch (fun setup ->
        let run = vara [ "check"; "nosuch"; "--config"; setup.Config ]

        Assert.Equal(1, run.ExitCode)
        Assert.Contains("No profile named 'nosuch' was found in the configuration file.", run.Err))

[<Fact>]
let ``check exits 0 on an intact target and 2 when a blob is missing`` () =
    scratch (fun setup ->
        write setup "a.txt" "alpha"
        write setup "b.txt" "beta"
        Assert.Equal(0, (backup setup []).ExitCode)

        let intact = vara [ "check"; "docs"; "--config"; setup.Config ]
        Assert.Equal(0, intact.ExitCode)
        Assert.Contains("Checked 2 blob(s): 0 missing, 0 corrupt, 0 orphaned.", intact.Text)

        let blob = Directory.EnumerateFiles(Path.Combine(setup.Target, ".vara", "versions"), "*", SearchOption.AllDirectories) |> Seq.head
        File.SetAttributes(blob, FileAttributes.Normal)
        File.Delete blob

        let damaged = vara [ "check"; "docs"; "--config"; setup.Config ]
        Assert.Equal(2, damaged.ExitCode)
        Assert.Contains("Checked 2 blob(s): 1 missing, 0 corrupt, 0 orphaned.", damaged.Text))

[<Fact>]
let ``show writes the stored bytes to standard output unchanged`` () =
    scratch (fun setup ->
        // Every byte value, so that no newline translation, encoding or binary refusal can pass unnoticed.
        let content = [| 0uy .. 255uy |]
        File.WriteAllBytes(sourceFile setup "all-bytes.bin", content)
        Assert.Equal(0, (backup setup []).ExitCode)

        let run =
            vara [ "show"; sourceFile setup "all-bytes.bin"; "--at"; "2100-01-01"; "--profile"; "docs"; "--config"; setup.Config ]

        Assert.Equal(0, run.ExitCode)
        Assert.Equal<byte array>(content, run.Out))

[<Fact>]
let ``a manifest from a newer version is refused with exit code 1 and the target is untouched`` () =
    scratch (fun setup ->
        write setup "a.txt" "alpha"
        Assert.Equal(0, (backup setup []).ExitCode)
        Vara.Cli.Tests.ManifestVersionCommandTests.stampManifest setup 99
        let databasePath = Vara.Engine.SqliteManifest.pathIn setup.Target
        let before = File.ReadAllBytes databasePath
        write setup "b.txt" "bravo"

        let run = backup setup [ "--json" ]

        Assert.Equal(1, run.ExitCode)
        Assert.Empty run.Out
        Assert.Contains("schema version 99", run.Err)
        Assert.Contains("supports up to version 1", run.Err)
        Assert.Equal<byte array>(before, File.ReadAllBytes databasePath))
