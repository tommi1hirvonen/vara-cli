/// Tests of the change tree options of `vara backup`, through the command. Each module is named after a requirement of
/// the plan-change-tree change (backup-execution), and each test after one of its scenarios.
module Vara.Cli.Tests.BackupTreeCommandTests

open System
open System.Collections.Generic
open System.IO
open System.Threading
open Xunit
open Vara.Cli
open Vara.Domain
open Vara.Engine
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests

/// A source with files in nested directories.
let private populate (setup: Scratch) : unit =
    write setup "top.txt" "top"
    write setup "a\\one.txt" "one"
    write setup "a\\b\\two.txt" "two"

let private backup (setup: Scratch) (parts: string list) : string array =
    Array.ofList ([ "backup"; "docs"; "--config"; setup.Config ] @ parts)

/// A key-driven session on a live console: the events are answered in order, and the screen calls are logged.
let private session (events: KeyEvent list) (log: List<string>) (beforeKey: unit -> unit) (env: CliEnv) : CliEnv =
    let remaining = Queue<KeyEvent> events

    { env with
        Capabilities = liveCapabilities
        Alternate =
            { Enter = fun () -> log.Add "Enter"
              Clear = fun () -> log.Add "Clear"
              Leave = fun () -> log.Add "Leave" }
        Keys =
            { Open = fun () -> { new IDisposable with member _.Dispose() = () }
              Next =
                fun () ->
                    beforeKey ()
                    if remaining.Count = 0 then failwith "the script ran out of keys" else remaining.Dequeue()
              Height = fun () -> 30 } }

let private browse (setup: Scratch) (events: KeyEvent list) (parts: string list) : Invocation * List<string> =
    let log = List<string>()
    let result = invokeEnv (session events log ignore) CancellationToken.None (backup setup parts)
    result, log

/// The lines of a report that show a directory's counts.
let private treeLines (text: string) : string list =
    text.Split '\n' |> Array.filter (fun line -> line.Contains "(+") |> Array.map _.TrimEnd() |> List.ofArray

module ``Change tree options on the backup command`` =
    [<Fact>]
    let ``Static tree after a dry run`` () =
        scratch (fun setup ->
            populate setup
            let result = invoke (backup setup [ "--dry-run"; "--depth"; "1" ])
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Dry run - no changes were made", result.Output)
            // The mirror root is the drive's parent, so one level below it is only the drive directory.
            Assert.Contains(". (+3 ~0 ->0 -0 link 0)", result.Output)
            Assert.DoesNotContain("source [source]", result.Output)
            Assert.DoesNotContain(" b (", result.Output)
            Assert.Contains("deeper director", result.Output))

    [<Fact>]
    let ``File paths in the tree`` () =
        scratch (fun setup ->
            populate setup
            let result = invoke (backup setup [ "--dry-run"; "--files" ])
            Assert.Contains("one.txt - Added", result.Output)
            Assert.Contains("two.txt - Added", result.Output))

    [<Fact>]
    let ``Unchanged output without tree options`` () =
        scratch (fun setup ->
            populate setup
            let dry = invoke (backup setup [ "--dry-run" ])
            Assert.DoesNotContain("link 0", dry.Output)
            Assert.DoesNotContain("one.txt", dry.Output)
            let real = invoke (backup setup [])
            Assert.DoesNotContain("link 0", real.Output)
            Assert.DoesNotContain("one.txt", real.Output))

    [<Fact>]
    let ``Rejecting an invalid depth`` () =
        scratch (fun setup ->
            populate setup

            for value in [ "abc"; "-1" ] do
                let result = invoke (backup setup [ "--dry-run"; "--depth"; value ])
                Assert.NotEqual(0, result.ExitCode)

            Assert.False(Directory.Exists setup.Target))

    [<Fact>]
    let ``Rejecting a tree option with JSON`` () =
        scratch (fun setup ->
            populate setup

            for option in [ [ "--depth"; "1" ]; [ "--files" ]; [ "--interactive" ] ] do
                let result = invoke (backup setup ([ "--json" ] @ option))
                Assert.Equal(1, result.ExitCode)
                Assert.Contains("--json cannot be combined", result.Errors.Replace("\n", ""))
                Assert.Equal("", result.Json)

            Assert.False(Directory.Exists setup.Target))

module ``Dry-run change tree shows the planned changes`` =
    [<Fact>]
    let ``Tree totals match the dry-run counts`` () =
        scratch (fun setup ->
            populate setup
            let result = invoke (backup setup [ "--dry-run"; "--depth"; "0" ])
            Assert.Contains("Added:       3", result.Output)
            Assert.Contains(". (+3 ~0 ->0 -0 link 0)", result.Output))

    [<Fact>]
    let ``Dry-run tree writes nothing`` () =
        scratch (fun setup ->
            populate setup
            let result, log = browse setup [ KeyEvent.Pressed Key.ExpandAll; KeyEvent.Pressed Key.Quit ] [ "--dry-run"; "--interactive" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Enter", log)
            Assert.False(File.Exists(Path.Combine(setup.Target, ".vara", "profile.db")))
            Assert.False(Directory.Exists(Path.Combine(setup.Target, ".vara", "versions"))))

    [<Fact>]
    let ``No planned changes`` () =
        scratch (fun setup ->
            populate setup
            backupOnce setup
            let log = List<string>()
            let result = invokeEnv (session [] log ignore) CancellationToken.None (backup setup [ "--dry-run"; "--files"; "--interactive" ])
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Added:       0", result.Output)
            Assert.Empty(treeLines result.Output)
            Assert.Empty log)

    [<Fact>]
    let ``Run lock is not held while browsing`` () =
        scratch (fun setup ->
            populate setup
            let target = (AbsolutePath.tryCreate setup.Target).Value
            let acquired = ref false

            let probe () =
                match RunLock.tryAcquire "other" target with
                | Ok handle ->
                    acquired.Value <- true
                    (handle :> IDisposable).Dispose()
                | Error _ -> ()

            let log = List<string>()

            let result =
                invokeEnv (session [ KeyEvent.Pressed Key.Quit ] log probe) CancellationToken.None (backup setup [ "--dry-run"; "--interactive" ])

            Assert.Equal(0, result.ExitCode)
            Assert.True acquired.Value)

module ``Real-run change tree shows the recorded snapshot`` =
    [<Fact>]
    let ``Static tree after a real run`` () =
        scratch (fun setup ->
            populate setup
            let run = invoke (backup setup [ "--depth"; "2"; "--files" ])
            Assert.Equal(0, run.ExitCode)
            Assert.Contains("completed in", run.Output)
            let snapshot = invoke [| "snapshot"; "1"; "--depth"; "2"; "--files"; "--profile"; "docs"; "--config"; setup.Config |]
            Assert.NotEmpty(treeLines run.Output)
            Assert.Equal<string list>(treeLines snapshot.Output, treeLines run.Output)
            let shown (text: string) = text.Split '\n' |> Array.filter (fun line -> line.Contains "one.txt" || line.Contains "two.txt")
            Assert.Equal<string array>(shown snapshot.Output, shown run.Output))

    [<Fact>]
    let ``Tree after a cancelled run`` () =
        scratch (fun setup ->
            for index in 1..6 do
                write setup $"f{index}.txt" (String('x', 1000))

            // The run is asked to stop once its first file is in the mirror.
            let stopWhenStarted (env: CliEnv) : CliEnv =
                { env with
                    MakeBackupEnv =
                        fun _ ->
                            BackupRun.live (fun () ->
                                Directory.Exists setup.Target
                                && Directory.EnumerateFiles(setup.Target, "f*.txt", SearchOption.AllDirectories) |> Seq.isEmpty |> not) }

            let result = invokeEnv stopWhenStarted CancellationToken.None (backup setup [ "--files" ])
            Assert.Equal(2, result.ExitCode)
            Assert.Contains("cancelled after", result.Output)
            Assert.NotEmpty(treeLines result.Output)
            Assert.Contains(".txt - Added", result.Output))

    [<Fact>]
    let ``Refused run shows no tree`` () =
        scratch (fun setup ->
            populate setup
            let target = (AbsolutePath.tryCreate setup.Target).Value
            use _held = RunLock.tryAcquire "other" target |> Result.defaultWith (fun _ -> failwith "no lock")
            let result, log = browse setup [] [ "--interactive" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Empty(treeLines result.Output)
            Assert.Empty log)

    [<Fact>]
    let ``Run with no recorded changes`` () =
        scratch (fun setup ->
            populate setup
            backupOnce setup
            let result = invoke (backup setup [ "--files" ])
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("No changes recorded for snapshot #2.", result.Output.Replace("\n", ""))
            Assert.Empty(treeLines result.Output))

module ``Interactive backup tree and its fallback`` =
    [<Fact>]
    let ``Interactive tree after a dry run`` () =
        scratch (fun setup ->
            populate setup
            let result, log = browse setup [ KeyEvent.Pressed Key.ExpandAll; KeyEvent.Pressed Key.Quit ] [ "--dry-run"; "--interactive" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Dry run - no changes were made", result.Output)
            Assert.Contains("Dry run - planned changes", result.Output)
            Assert.Contains("b (+1", result.Output)
            // The deepest directory is expanded to its files, and no `--files` was given.
            Assert.Contains("+  two.txt", result.Output)
            Assert.Contains("+  one.txt", result.Output)
            Assert.Contains("Enter", log)
            Assert.Contains("Leave", log))

    [<Fact>]
    let ``Interactive tree after a real run`` () =
        scratch (fun setup ->
            populate setup
            let result, log = browse setup [ KeyEvent.Pressed Key.ExpandAll; KeyEvent.Pressed Key.Quit ] [ "--interactive" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("completed in", result.Output)
            Assert.Contains("Snapshot #1", result.Output)
            // The recorded snapshot's files are listed without `--files`.
            Assert.Contains("+  two.txt", result.Output)
            Assert.Contains("+  top.txt", result.Output)
            Assert.Contains("Leave", log))

    [<Fact>]
    let ``Fallback when output is redirected`` () =
        scratch (fun setup ->
            populate setup
            // The default test console is not a terminal, as with output redirected to a file.
            let result = invoke (backup setup [ "--dry-run"; "--interactive" ])
            Assert.Equal(0, result.ExitCode)
            Assert.NotEmpty(treeLines result.Output)
            Assert.Contains("interactive view is unavailable", result.Errors.Replace("\n", " ")))

    [<Fact>]
    let ``Cancelled run shows the tree statically`` () =
        scratch (fun setup ->
            for index in 1..6 do
                write setup $"f{index}.txt" (String('x', 1000))

            // The run is asked to stop once its first file is in the mirror.
            let stopWhenStarted (env: CliEnv) : CliEnv =
                { env with
                    MakeBackupEnv =
                        fun _ ->
                            BackupRun.live (fun () ->
                                Directory.Exists setup.Target
                                && Directory.EnumerateFiles(setup.Target, "f*.txt", SearchOption.AllDirectories) |> Seq.isEmpty |> not) }

            let log = List<string>()

            let result =
                invokeEnv (stopWhenStarted << session [] log ignore) CancellationToken.None (backup setup [ "--interactive"; "--files" ])

            Assert.Equal(2, result.ExitCode)
            Assert.Contains("cancelled after", result.Output)
            // The interactive view is withheld without a note, and the recorded changes are shown statically.
            Assert.Empty log
            Assert.DoesNotContain("unavailable", result.Errors.Replace("\n", " "))
            Assert.NotEmpty(treeLines result.Output))

    [<Fact>]
    let ``Exit code unaffected by quitting`` () =
        scratch (fun setup ->
            populate setup
            let result, _ = browse setup [ KeyEvent.Pressed Key.Quit ] [ "--interactive" ]
            Assert.Equal(0, result.ExitCode))

    [<Fact>]
    let ``Exit code unaffected by Ctrl+C in the tree`` () =
        scratch (fun setup ->
            populate setup
            let result, log = browse setup [ KeyEvent.Interrupted ] [ "--interactive" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Leave", log))

    [<Fact>]
    let ``Partial failure outcome preserved`` () =
        scratch (fun setup ->
            write setup "locked.txt" "locked"
            write setup "fine.txt" "fine"
            use _hold = new FileStream(Path.Combine(setup.Source, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None)
            let result, _ = browse setup [ KeyEvent.Pressed Key.Quit ] [ "--interactive" ]
            Assert.Equal(2, result.ExitCode))

module ``Reading the recorded tree`` =
    [<Fact>]
    let ``A manifest that cannot be read is reported as a failure`` () =
        scratch (fun setup ->
            let layout = ContentStore.layout setup.Target
            let database = SqliteManifest.pathIn layout.MirrorRoot
            Directory.CreateDirectory(Path.GetDirectoryName database |> nonNull) |> ignore
            File.WriteAllText(database, "this is not a database")

            let target: HistorySession.Target =
                { Name = "docs"
                  Layout = layout
                  SourceRoots = Set.empty }

            match ChangeTree.readSnapshot target 1L false false with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "expected the read to fail")
