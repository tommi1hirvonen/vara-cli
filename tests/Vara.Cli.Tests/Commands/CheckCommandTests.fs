/// Tests of `vara check`, through the command against a real target written by the F# backup. Each module is named after
/// a requirement of openspec/specs/backup-integrity/spec.md and each test after one of its scenarios.
module Vara.Cli.Tests.CheckCommandTests

open System
open System.IO
open System.Threading
open Xunit
open Vara.Cli
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests
open Vara.Domain
open Vara.Engine

let private check (setup: Scratch) (parts: string list) : Invocation =
    invoke (Array.ofList ([ "check"; "docs" ] @ parts @ [ "--config"; setup.Config ]))

let private store (setup: Scratch) : StoreLayout = ContentStore.layout setup.Target

/// The blob of the oldest recorded version of a.txt: after later runs it is history only.
let private firstBlob (setup: Scratch) : string =
    let oldest = rowsOf setup |> List.filter (fun version -> MirrorPath.value version.Row.RowPath = keyOf setup "a.txt") |> List.minBy
                                                                                                                                _.VersionId

    match oldest.Row.Content with
    | RowContent.Regular content -> ContentStore.blobPath (store setup) content.Hash
    | RowContent.Linked _ -> failwith "expected a regular row"

let private deleteBlob (path: string) : unit =
    File.SetAttributes(path, FileAttributes.Normal)
    File.Delete path

let private tamper (path: string) : unit =
    File.SetAttributes(path, FileAttributes.Normal)
    File.WriteAllText(path, "tampered")

/// Two versions of a.txt, so that the first blob is not the live one, plus a file that stays.
let private twoVersions (setup: Scratch) : unit =
    write setup "a.txt" "first content"
    write setup "b.txt" "bravo content"
    backupOnce setup
    changeFile setup "a.txt" "second content, longer"
    backupOnce setup

/// The lines of a run's output that are progress.
let private progressLines (result: Invocation) : string list =
    result.Output.Split '\n' |> Array.map _.TrimEnd('\r') |> Array.filter (fun line -> line.Contains "Files ") |> List.ofArray

module ``Check command verifies stored content against the manifest`` =
    [<Fact>]
    let ``All referenced content intact`` () =
        scratch (fun setup ->
            twoVersions setup
            let result = check setup []
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Checked 3 blob(s): 0 missing, 0 corrupt, 0 orphaned.", result.Output))

    [<Fact>]
    let ``Running check without a profile name is a syntax error`` () =
        scratch (fun setup ->
            let result = invoke [| "check"; "--config"; setup.Config |]
            Assert.NotEqual(0, result.ExitCode)
            Assert.Contains("profile", result.Syntax))

    [<Fact>]
    let ``Running check with the --profile option is a syntax error`` () =
        scratch (fun setup ->
            twoVersions setup
            let result = invoke [| "check"; "--profile"; "docs"; "--config"; setup.Config |]
            Assert.NotEqual(0, result.ExitCode)
            Assert.Contains("Unrecognized command or argument", result.Syntax))

    [<Fact>]
    let ``Referenced blob missing from the store`` () =
        scratch (fun setup ->
            twoVersions setup
            deleteBlob (firstBlob setup)
            let result = check setup []
            // Reported as missing with a path that references it, and a status a script can test.
            Assert.Equal(2, result.ExitCode)
            Assert.Contains("1 missing, 0 corrupt", result.Output)
            Assert.Contains("Missing:", result.Output)
            Assert.Contains(keyOf setup "a.txt", result.Output))

    [<Fact>]
    let ``Referenced blob present but corrupted`` () =
        scratch (fun setup ->
            twoVersions setup
            tamper (firstBlob setup)
            let result = check setup []
            Assert.Equal(2, result.ExitCode)
            Assert.Contains("0 missing, 1 corrupt", result.Output)
            Assert.Contains("Corrupt:", result.Output)
            Assert.Contains(keyOf setup "a.txt", result.Output))

    [<Fact>]
    let ``Unreferenced content present in the store`` () =
        scratch (fun setup ->
            twoVersions setup
            let orphan = Path.Combine(setup.Target, ".vara", "versions", "ab", "abcdef0123456789abcdef0123456789")
            Directory.CreateDirectory(Path.GetDirectoryName orphan |> nonNull) |> ignore
            File.WriteAllText(orphan, "an orphan")
            let result = check setup []
            // Informational: counted, not an error, and not deleted.
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("1 orphaned", result.Output)
            Assert.True(File.Exists orphan))

    [<Fact>]
    let ``Profile contains a tracked symlink or junction`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            Directory.CreateDirectory(Path.Combine(setup.Root, "elsewhere")) |> ignore
            createJunction (Path.Combine(setup.Source, "link")) (Path.Combine(setup.Root, "elsewhere"))
            backupOnce setup
            let result = check setup []
            Assert.Equal(0, result.ExitCode)
            // Only the one file's content is a blob; the link is not.
            Assert.Contains("Checked 1 blob(s): 0 missing, 0 corrupt", result.Output))

    [<Fact>]
    let ``a profile that was never backed up has nothing to check and nothing is created`` () =
        scratch (fun setup ->
            let result = check setup []
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Checked 0 blob(s)", result.Output)
            // A read-only command never creates the target's storage.
            Assert.False(Directory.Exists setup.Target))

module ``Quick mode trades thoroughness for speed`` =
    [<Fact>]
    let ``Quick check skips content re-hashing`` () =
        scratch (fun setup ->
            twoVersions setup
            tamper (firstBlob setup)
            let quick = check setup [ "--quick" ]
            // The blob is present, so presence is all that is asked.
            Assert.Equal(0, quick.ExitCode)
            Assert.Contains("0 missing, 0 corrupt", quick.Output)
            // The default mode finds it.
            Assert.Equal(2, (check setup []).ExitCode)
            // A missing blob is reported by the quick mode too.
            deleteBlob (firstBlob setup)
            let missing = check setup [ "--quick" ]
            Assert.Equal(2, missing.ExitCode)
            Assert.Contains("1 missing", missing.Output))

module ``Check exits with a scriptable status`` =
    [<Fact>]
    let ``Problems found`` () =
        scratch (fun setup ->
            twoVersions setup
            deleteBlob (firstBlob setup)
            let problems = check setup []
            // Distinct from the status of an unrelated hard error, such as a profile that does not exist.
            let unrelated = invoke [| "check"; "nothing"; "--config"; setup.Config |]
            Assert.Equal(2, problems.ExitCode)
            Assert.Equal(1, unrelated.ExitCode)
            Assert.Contains("nothing", unrelated.Errors))

    [<Fact>]
    let ``No problems found`` () =
        scratch (fun setup ->
            twoVersions setup
            Assert.Equal(0, (check setup []).ExitCode))

module ``Graceful cancellation via CtrlC`` =
    [<Fact>]
    let ``Single Ctrl+C during verification`` () =
        scratch (fun setup ->
            twoVersions setup
            use source = new CancellationTokenSource()
            source.Cancel()
            let result = invokeWith source.Token [| "check"; "docs"; "--config"; setup.Config |]
            // No blob was verified, the run says it was cancelled, and it is not a hard error. The C# build's
            // partial-failure code is kept (the spec's "exits successfully" is a gap recorded in design D4).
            Assert.Equal(2, result.ExitCode)
            Assert.Contains("Check cancelled: stopped early", result.Output)
            Assert.Contains("Checked 0 blob(s)", result.Output))

    [<Fact>]
    let ``Second Ctrl+C forces immediate termination`` () =
        scratch (fun setup ->
            twoVersions setup
            let forced = ref None
            let cancellation = GracefulCancellation(fun code -> forced.Value <- Some code)
            Assert.True(cancellation.RequestCancellation())
            let result = invokeWith cancellation.Token [| "check"; "docs"; "--config"; setup.Config |]
            Assert.Equal(2, result.ExitCode)
            // The second Ctrl+C exits at once with the hard-error code, without waiting for the blob in progress.
            Assert.False(cancellation.RequestCancellation())
            Assert.Equal(Some(Outcome.exitCode Outcome.HardError), forced.Value))

module ``Check reports workload progress and estimated time remaining`` =
    [<Fact>]
    let ``Full check reports byte-weighted progress`` () =
        scratch (fun setup ->
            twoVersions setup
            let lines = progressLines (check setup [])
            // Bytes against the manifest total, the count of blobs, and the throughput and ETA of a byte run.
            Assert.NotEmpty lines
            let last = List.last lines
            Assert.Contains(" B / ", last)
            Assert.Contains("(100.0%)", last)
            Assert.Contains("Files 3 / 3", last)
            Assert.Contains("/s - ETA", last))

    [<Fact>]
    let ``Quick check reports count-based progress and ETA`` () =
        scratch (fun setup ->
            twoVersions setup
            let last = progressLines (check setup [ "--quick" ]) |> List.last
            // The percentage counts blobs; there is no byte count or throughput.
            Assert.Contains("Files 3 / 3 (100.0%)", last)
            Assert.Contains("ETA", last)
            Assert.DoesNotContain("/s", last))

    [<Fact>]
    let ``Estimate is not yet available`` () =
        scratch (fun setup ->
            twoVersions setup
            // Before anything was observed the estimate is not a number.
            let first = progressLines (check setup []) |> List.head
            Assert.Contains("ETA calculating...", first))

    [<Fact>]
    let ``Empty or zero-size workload completes cleanly`` () =
        scratch (fun setup ->
            // Nothing recorded: no blobs and a total of zero.
            let lines = progressLines (check setup [])
            Assert.NotEmpty lines
            Assert.Contains("(100.0%)", List.last lines)
            Assert.DoesNotContain("NaN", String.Join("\n", lines)))

    [<Fact>]
    let ``Redirected output reports progress`` () =
        scratch (fun setup ->
            twoVersions setup
            let result = check setup []
            // Plain, appended lines, with no control sequence in them.
            let lines = progressLines result
            Assert.NotEmpty lines
            Assert.All(lines, fun line -> Assert.Contains("%", line))
            Assert.All(lines, fun line -> Assert.Contains("Files ", line))
            Assert.All(lines, fun line -> Assert.Contains("ETA", line))
            Assert.False(result.Output.Contains(char 27)))
