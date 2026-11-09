/// Tests of `vara snapshot`, `browse` and `deleted`, and of the date/time options of the history commands, through the
/// command. Each module is named after a requirement of openspec/specs/snapshot-history, backup-browsing or
/// cli-presentation and each test after one of its scenarios.
module Vara.Cli.Tests.BrowsingCommandTests

open System.IO
open Spectre.Console.Testing
open Xunit
open Vara.Cli
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests

module ``Inspect changes recorded by a snapshot`` =
    /// A target with one snapshot that added files in two nested directories and a moved file.
    let private populated (setup: Scratch) : unit =
        write setup "top.txt" "top"
        write setup "a\\one.txt" "one"
        write setup "a\\b\\two.txt" "two"
        write setup "a\\b\\c\\three.txt" "three"
        backupOnce setup

    let private snapshot (setup: Scratch) (parts: string list) : Invocation = run setup ([ "snapshot" ] @ parts)

    [<Fact>]
    let ``Showing a snapshot's directory rollups`` () =
        scratch (fun setup ->
            populated setup
            let result = snapshot setup [ "1" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Snapshot #1", result.Output)
            Assert.Contains("Complete", result.Output)
            Assert.Contains("(+4 ~0 ->0 -0 link 0)", result.Output)
            Assert.Contains("c (+1 ~0 ->0 -0 link 0)", result.Output))

    [<Fact>]
    let ``Identifying configured source roots`` () =
        scratch (fun setup ->
            populated setup
            let result = snapshot setup [ "1" ]
            Assert.Contains("[source]", result.Output)
            Assert.Contains("source [source]", result.Output))

    [<Fact>]
    let ``Scoping the report to a directory`` () =
        scratch (fun setup ->
            populated setup
            let scope = Path.GetDirectoryName(keyOf setup "a\\b\\two.txt") |> nonNull
            let result = snapshot setup [ "1"; scope ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("(+2 ~0 ->0 -0 link 0)", result.Output)
            Assert.DoesNotContain("one.txt", result.Output)
            Assert.DoesNotContain("top.txt", result.Output))

    [<Fact>]
    let ``Showing changed file paths on request`` () =
        scratch (fun setup ->
            populated setup
            let result = snapshot setup [ "1"; "--files" ]
            Assert.Contains("top.txt - Added", result.Output)
            Assert.Contains("three.txt - Added", result.Output))

    [<Fact>]
    let ``Omitting file paths by default`` () =
        scratch (fun setup ->
            populated setup
            Assert.DoesNotContain("top.txt", (snapshot setup [ "1" ]).Output))

    [<Fact>]
    let ``Unknown snapshot id`` () =
        scratch (fun setup ->
            populated setup
            let result = snapshot setup [ "99" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("snapshot #99 is not recorded for profile 'docs'", result.Errors.Replace("\n", ""))
            Assert.Equal("", result.Output))

    [<Fact>]
    let ``No changes in the requested scope`` () =
        scratch (fun setup ->
            populated setup
            let result = snapshot setup [ "1"; "nothing\\here" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("No changes recorded for snapshot #1 in 'nothing\\here'.", result.Output.Replace("\n", "")))

    [<Fact>]
    let ``Limiting displayed directory depth`` () =
        scratch (fun setup ->
            populated setup
            let scope = Path.GetDirectoryName(keyOf setup "a\\one.txt") |> nonNull
            let text = (snapshot setup [ "1"; scope; "--depth"; "1" ]).Output
            Assert.Contains("b (", text)
            Assert.DoesNotContain("c (", text))

    [<Fact>]
    let ``Showing only the selected scope root`` () =
        scratch (fun setup ->
            populated setup
            let scope = Path.GetDirectoryName(keyOf setup "a\\one.txt") |> nonNull
            let text = (snapshot setup [ "1"; scope; "--depth"; "0" ]).Output
            Assert.DoesNotContain("b (", text)
            Assert.Contains("deeper director", text))

    [<Fact>]
    let ``Indicating omitted affected directories`` () =
        scratch (fun setup ->
            populated setup
            let scope = Path.GetDirectoryName(keyOf setup "a\\one.txt") |> nonNull
            let text = (snapshot setup [ "1"; scope; "--depth"; "1" ]).Output
            Assert.Contains("... 1 deeper directory not shown", text))

    [<Fact>]
    let ``Preserving rollups across a depth limit`` () =
        scratch (fun setup ->
            populated setup
            let scope = Path.GetDirectoryName(keyOf setup "a\\one.txt") |> nonNull
            let text = (snapshot setup [ "1"; scope; "--depth"; "0" ]).Output
            // one.txt, two.txt and three.txt are all counted at the root of the scope.
            Assert.Contains("(+3 ~0 ->0 -0 link 0)", text))

    [<Fact>]
    let ``Preserving unlimited depth by default`` () =
        scratch (fun setup ->
            populated setup
            Assert.Contains("c (", (snapshot setup [ "1" ]).Output))

    [<Fact>]
    let ``Combining depth and file details`` () =
        scratch (fun setup ->
            populated setup
            let scope = Path.GetDirectoryName(keyOf setup "a\\one.txt") |> nonNull
            let text = (snapshot setup [ "1"; scope; "--depth"; "1"; "--files" ]).Output
            Assert.Contains("one.txt - Added", text)
            Assert.Contains("two.txt - Added", text)
            Assert.DoesNotContain("three.txt", text))

    [<Fact>]
    let ``Rejecting an invalid depth`` () =
        scratch (fun setup ->
            populated setup

            for bad in [ "-1"; "x"; "1.5" ] do
                let result = snapshot setup [ "1"; "--depth"; bad ]
                Assert.Equal(1, result.ExitCode)
                Assert.Contains("--depth must be a non-negative integer", result.Errors)
                Assert.Equal("", result.Output))

    [<Fact>]
    let ``Inspecting snapshots without backing storage`` () =
        scratch (fun setup ->
            let result = snapshot setup [ "1" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("snapshot #1 is not recorded", result.Errors.Replace("\n", ""))
            Assert.False(Directory.Exists setup.Target))

module ``Snapshot change rollups use change-kind colors`` =
    [<Fact>]
    let ``Distinguishing change kinds in directory rollups`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let console = (new TestConsole()).EmitAnsiSequences()
            let live (env: CliEnv) = { env with Console = console; Capabilities = liveCapabilities }
            runEnv live setup [ "snapshot"; "1" ] |> ignore
            let line = console.Output.Split('\n') |> Array.find (fun text -> text.Contains "link ")
            // The counts are coloured one by one, so the line holds several colour sequences, not one for the label.
            Assert.True((line |> Seq.filter (fun c -> c = char 27) |> Seq.length) >= 8))

    [<Fact>]
    let ``Reading rollups without color support`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let result = run setup [ "snapshot"; "1" ]
            Assert.False(result.Output.Contains(char 27))
            Assert.Contains("+1 ~0 ->0 -0 link 0", result.Output))

module ``One-level directory listing`` =
    [<Fact>]
    let ``Listing a directory's current contents`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            write setup "dir\\sub\\deep.txt" "deep"
            backupOnce setup
            let directory = Path.GetDirectoryName(keyOf setup "dir\\a.txt") |> nonNull
            let result = run setup [ "browse"; directory ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("a.txt", result.Output)
            Assert.Contains("sub", result.Output)
            Assert.Contains("Directory", result.Output)
            // Without descending into the subdirectory.
            Assert.DoesNotContain("deep.txt", result.Output))

    [<Fact>]
    let ``Directory never tracked`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let result = run setup [ "browse"; "never\\tracked" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("No such directory is recorded in the backup", result.Errors.Replace("\n", "")))

    [<Fact>]
    let ``Browsing a profile that has never completed a backup run`` () =
        scratch (fun setup ->
            let result = run setup [ "browse"; "some\\dir" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("No such directory is recorded in the backup", result.Errors.Replace("\n", ""))
            Assert.False(Directory.Exists setup.Target))

module ``Point-in-time directory listing`` =
    [<Fact>]
    let ``Listing a directory as it looked on a past date`` () =
        scratch (fun setup ->
            write setup "dir\\old.txt" "old"
            backupOnce setup
            changeFile setup "dir\\new.txt" "new"
            File.Delete(sourceFile setup "dir\\old.txt")
            backupOnce setup
            let directory = Path.GetDirectoryName(keyOf setup "dir\\old.txt") |> nonNull
            let past = run setup [ "browse"; directory; "--at"; betweenFirstTwo setup ]
            Assert.Contains("old.txt", past.Output)
            Assert.DoesNotContain("new.txt", past.Output)
            let now = run setup [ "browse"; directory ]
            Assert.Contains("new.txt", now.Output)
            Assert.DoesNotContain("old.txt", now.Output))

module ``Including deleted entries in a directory listing`` =
    let private populated (setup: Scratch) : string =
        write setup "dir\\live.txt" "live"
        write setup "dir\\gone.txt" "gone"
        write setup "dir\\left.txt" "moving content"
        write setup "dir\\emptied\\x.txt" "x"
        backupOnce setup
        File.Delete(sourceFile setup "dir\\gone.txt")
        File.Delete(sourceFile setup "dir\\emptied\\x.txt")
        Directory.CreateDirectory(sourceFile setup "elsewhere") |> ignore
        changeFile setup "elsewhere\\left.txt" "moving content"
        File.Delete(sourceFile setup "dir\\left.txt")
        backupOnce setup
        Path.GetDirectoryName(keyOf setup "dir\\live.txt") |> nonNull

    [<Fact>]
    let ``Including deleted entries in the current listing`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = run setup [ "browse"; directory; "--deleted" ]
            Assert.Contains("gone.txt", result.Output)
            Assert.Contains("Deleted", result.Output)
            // Interleaved by name: "emptied" < "gone.txt" < "left.txt" < "live.txt".
            let order = [ "emptied"; "gone.txt"; "left.txt"; "live.txt" ] |> List.map result.Output.IndexOf
            Assert.Equal<int list>(List.sort order, order))

    [<Fact>]
    let ``Including deleted entries in a point-in-time listing`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = run setup [ "browse"; directory; "--deleted"; "--at"; betweenFirstTwo setup ]
            // Everything existed then: nothing is marked deleted.
            Assert.Contains("gone.txt", result.Output)
            Assert.DoesNotContain("Deleted", result.Output))

    [<Fact>]
    let ``Entry moved out of the listed directory`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = run setup [ "browse"; directory; "--deleted" ]
            Assert.Contains("Moved ->", result.Output)
            Assert.Contains("elsewhere", result.Output))

    [<Fact>]
    let ``Subdirectory whose contents are entirely deleted`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = run setup [ "browse"; directory; "--deleted" ]
            let line = result.Output.Split('\n') |> Array.find (fun text -> text.Contains "emptied")
            Assert.Contains("Directory", line)
            Assert.Contains("Deleted", line))

module ``Recently deleted report`` =
    let private populated (setup: Scratch) : unit =
        write setup "a\\one.txt" "one"
        write setup "b\\two.txt" "two"
        write setup "keep.txt" "keep"
        backupOnce setup
        File.Delete(sourceFile setup "a\\one.txt")
        backupOnce setup
        changeFile setup "x.txt" "x"
        File.Delete(sourceFile setup "b\\two.txt")
        backupOnce setup

    [<Fact>]
    let ``Listing recently deleted files across a profile`` () =
        scratch (fun setup ->
            populated setup
            let result = run setup [ "deleted" ]
            Assert.Equal(0, result.ExitCode)
            // Most recently deleted first.
            Assert.True(result.Output.IndexOf "two.txt" < result.Output.IndexOf "one.txt")
            Assert.DoesNotContain("keep.txt", result.Output))

    [<Fact>]
    let ``Scoping the recently deleted report to a subtree`` () =
        scratch (fun setup ->
            populated setup
            let directory = Path.GetDirectoryName(keyOf setup "a\\one.txt") |> nonNull
            let result = run setup [ "deleted"; directory ]
            Assert.Contains("one.txt", result.Output)
            Assert.DoesNotContain("two.txt", result.Output))

    [<Fact>]
    let ``No deleted files found`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let result = run setup [ "deleted" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("No deleted files recorded.", result.Output))

module ``Invalid datetime option input produces a friendly error`` =
    [<Fact>]
    let ``Unparseable value given to a date/time option`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let result = run setup [ "browse"; "--at"; "yesterday" ]
            Assert.Equal(1, result.ExitCode)
            let errors = result.Errors.Replace("\n", "")
            Assert.Contains("'yesterday'", errors)
            Assert.Contains("'--at'", errors)
            // The action did not run: nothing was listed.
            Assert.Equal("", result.Output))

    [<Fact>]
    let ``Ambiguous slash-separated date given to a date/time option`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let result = run setup [ "deleted"; "--since"; "01/02/2025" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("'01/02/2025'", result.Errors.Replace("\n", ""))
            Assert.Contains("'--since'", result.Errors.Replace("\n", "")))

    [<Fact>]
    let ``Valid value still parses as before`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            Assert.Equal(0, (run setup [ "deleted"; "--since"; "2020-01-15" ]).ExitCode)
            Assert.Equal(0, (run setup [ "deleted"; "--since"; "2020-01-15 14:30" ]).ExitCode))

module ``Datetime options document an example value in their help text`` =
    let private help (command: string) : string =
        let result = invoke [| command; "--help" |]
        result.Syntax.Replace("\r", "").Replace("\n", " ")

    [<Fact>]
    let ``Viewing help for a command with a date/time option`` () =
        for command in [ "browse"; "deleted"; "restore"; "show"; "diff" ] do
            let text = help command
            // The example is wrapped by the help formatter, so it is looked for by its two values.
            Assert.Contains("2025-01-15", text)
            Assert.Contains("14:30", text)
