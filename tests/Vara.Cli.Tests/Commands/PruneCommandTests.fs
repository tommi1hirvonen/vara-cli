/// Tests of `vara prune`, through the command against a real target written by the F# backup, with scripted prompts.
/// Each module is named after a requirement of openspec/specs/retention-pruning/spec.md (or, for the one profile-config
/// scenario, openspec/specs/profile-config/spec.md) and each test after one of its scenarios.
module Vara.Cli.Tests.PruneCommandTests

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open Vara.Cli
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests
open Vara.Domain
open Vara.Engine

/// Gives the profile a retention policy that keeps the newest snapshot of the day: with every snapshot of a test made
/// on one day, that is only the newest one, so every older snapshot is eligible.
let withPolicy (setup: Scratch) : unit =
    File.WriteAllText(
        setup.Config,
        $"profiles:\n  - name: docs\n    target: {setup.Target}\n    sources:\n      - path: {setup.Source}\n    retention:\n      keep_daily: 1\n"
    )

let private prune (setup: Scratch) (parts: string list) : Invocation =
    invoke (Array.ofList ([ "prune"; "docs" ] @ parts @ [ "--config"; setup.Config ]))

let private pruneEnv (customize: CliEnv -> CliEnv) (setup: Scratch) (parts: string list) : Invocation =
    invokeEnv customize CancellationToken.None (Array.ofList ([ "prune"; "docs" ] @ parts @ [ "--config"; setup.Config ]))

/// A session that can be asked: input is not redirected, but the output is still plain text.
let asking (prompts: Prompts) (env: CliEnv) : CliEnv =
    { env with
        Prompts = prompts
        CliEnv.Capabilities.InputIsInteractive = true }

/// A session that cannot be asked, whatever the test host's own input is.
let notAsking (env: CliEnv) : CliEnv =
    { env with CliEnv.Capabilities.InputIsInteractive = false }

/// Prompts that record each question and answer through the function; asking a selection fails the test.
type Asked(answer: string -> bool) =
    member val Questions = ResizeArray<string>()

    member this.Prompts: Prompts =
        { Select = fun title _ _ -> failwith $"unexpected selection prompt: {title}"
          Confirm =
            fun question ->
                this.Questions.Add question
                answer question
          Text = fun prompt -> failwith $"unexpected text prompt: {prompt}" }

/// Three snapshots of a.txt, each with content of its own, so that every older snapshot holds a blob nothing else needs.
let threeSnapshots (setup: Scratch) : unit =
    withPolicy setup
    write setup "a.txt" "first content"
    backupOnce setup
    changeFile setup "a.txt" "second content, longer"
    backupOnce setup
    changeFile setup "a.txt" "third content, longer still"
    backupOnce setup

let snapshotIds (setup: Scratch) : int64 list =
    snapshotsOf setup |> List.map _.SnapshotId |> List.sort

let blobCount (setup: Scratch) : int =
    (ContentStore.storedHashes (ContentStore.layout setup.Target)).Count

let private mirrorContent (setup: Scratch) : string = File.ReadAllText(Path.Combine(setup.Target, keyOf setup "a.txt"))

let private icacls (arguments: string) : int =
    let info = Diagnostics.ProcessStartInfo("cmd.exe", $"/c icacls {arguments}")
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use proc = Diagnostics.Process.Start info |> nonNull
    proc.WaitForExit()
    proc.ExitCode

let denyAccess (path: string) : unit =
    Assert.Equal(0, icacls $"\"{path}\" /deny \"%%USERNAME%%:(R,W)\"")

let restoreAccess (path: string) : unit =
    icacls $"\"{path}\" /remove:d \"%%USERNAME%%\"" |> ignore

/// Removes a snapshot behind the command's back, as some other tool or run could.
let private removeBehindItsBack (setup: Scratch) (snapshotId: int64) : unit =
    SqliteConnection.ClearAllPools()
    let database = Path.Combine(setup.Target, ".vara", "profile.db")
    use connection = new SqliteConnection($"Data Source={database};Pooling=False")
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- $"DELETE FROM file_versions WHERE snapshot_id = {snapshotId}; DELETE FROM snapshots WHERE id = {snapshotId}"
    command.ExecuteNonQuery() |> ignore

module ``Prune command`` =
    [<Fact>]
    let ``Running prune with only a profile name`` () =
        scratch (fun setup ->
            withPolicy setup
            write setup "a.txt" "alpha"
            backupOnce setup
            // A single snapshot is the newest, so nothing is eligible: the run proceeds with no question.
            let result = prune setup []
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Removed 0 snapshot(s)", result.Output))

    [<Fact>]
    let ``Running prune without a profile name is a syntax error`` () =
        scratch (fun setup ->
            withPolicy setup
            let result = invoke [| "prune"; "--config"; setup.Config |]
            Assert.NotEqual(0, result.ExitCode)
            Assert.Contains("profile", result.Syntax))

    [<Fact>]
    let ``Running prune with the --profile option is a syntax error`` () =
        scratch (fun setup ->
            withPolicy setup
            threeSnapshots setup
            let result = invoke [| "prune"; "--profile"; "docs"; "--config"; setup.Config; "--yes" |]
            Assert.NotEqual(0, result.ExitCode)
            Assert.Contains("Unrecognized command or argument", result.Syntax)
            Assert.Equal(3, (snapshotIds setup).Length))

    [<Fact>]
    let ``No snapshots eligible for removal`` () =
        scratch (fun setup ->
            withPolicy setup
            write setup "a.txt" "alpha"
            backupOnce setup
            // The default prompts fail the test if a question is asked.
            let result = pruneEnv notAsking setup []
            Assert.Equal(0, result.ExitCode)
            Assert.Equal(1, (snapshotIds setup).Length)
            Assert.Contains("Removed 0 snapshot(s) and 0 unreferenced content blob(s)", result.Output))

    [<Fact>]
    let ``Snapshots eligible for removal, explicit override given`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let result = pruneEnv notAsking setup [ "--yes" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Removed 2 snapshot(s) and 2 unreferenced content blob(s)", result.Output)
            Assert.Equal(1, (snapshotIds setup).Length)

            // The short form is the same option.
            let more = pruneEnv notAsking setup [ "-y" ]
            Assert.Equal(0, more.ExitCode))

    [<Fact>]
    let ``Snapshots eligible for removal, running interactively, no explicit override given`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let asked = Asked(fun _ -> false)
            let result = pruneEnv (asking asked.Prompts) setup []
            // One question, stating how many snapshots will go; the answer was no, so nothing changed.
            Assert.Single asked.Questions |> ignore
            Assert.Contains("2 snapshot(s)", asked.Questions[0])
            Assert.Contains("permanently remove", asked.Questions[0])
            Assert.Equal(0, result.ExitCode)
            Assert.Equal(3, (snapshotIds setup).Length))

    [<Fact>]
    let ``User confirms the interactive prune prompt`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let result = pruneEnv (asking (Asked(fun _ -> true)).Prompts) setup []
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Removed 2 snapshot(s)", result.Output)
            Assert.Equal(1, (snapshotIds setup).Length)
            // Garbage collection ran as normal.
            Assert.Equal(1, blobCount setup))

    [<Fact>]
    let ``A snapshot becomes eligible after the prompt was shown but before removal runs`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let shown = snapshotIds setup |> List.take 2
            // While the question is on the screen, a backup completes: the snapshot that was the newest is now
            // eligible too, but the user was not shown it.
            let asked =
                Asked(fun _ ->
                    changeFile setup "a.txt" "fourth content, longer yet again"
                    backupOnce setup
                    true)

            let result = pruneEnv (asking asked.Prompts) setup []
            Assert.Equal(0, result.ExitCode)
            let remaining = snapshotIds setup
            Assert.Equal(2, remaining.Length)
            Assert.DoesNotContain(shown[0], remaining)
            Assert.DoesNotContain(shown[1], remaining)
            // The third snapshot stays for a later run to evaluate again.
            Assert.Contains(3L, remaining))

    [<Fact>]
    let ``A confirmed snapshot no longer exists by the time removal runs`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let ids = snapshotIds setup

            let asked =
                Asked(fun _ ->
                    removeBehindItsBack setup ids[1]
                    true)

            let result = pruneEnv (asking asked.Prompts) setup []
            // Not an error: the one that is still there is removed.
            Assert.Equal(0, result.ExitCode)
            Assert.Equal<int64 list>([ ids[2] ], snapshotIds setup)
            Assert.Contains("Removed 1 snapshot(s)", result.Output))

    [<Fact>]
    let ``User declines the interactive prune prompt`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let before = blobCount setup
            let result = pruneEnv (asking (Asked(fun _ -> false)).Prompts) setup []
            // Reported as cancelled, and not as an error.
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Prune cancelled", result.Output)
            Assert.Equal(3, (snapshotIds setup).Length)
            Assert.Equal(before, blobCount setup))

    [<Fact>]
    let ``Snapshots eligible for removal, running non-interactively, no explicit override given`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let result = pruneEnv notAsking setup []
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("--yes", result.Errors.Replace("\n", ""))
            Assert.Equal(3, (snapshotIds setup).Length)
            Assert.Equal(3, blobCount setup))

module ``Unreferenced content garbage collection`` =
    [<Fact>]
    let ``Content only referenced by pruned snapshots`` () =
        scratch (fun setup ->
            threeSnapshots setup
            Assert.Equal(3, blobCount setup)
            let result = prune setup [ "--yes" ]
            Assert.Equal(0, result.ExitCode)
            // The two blobs that only the pruned snapshots referenced are gone, and the current one is kept.
            Assert.Equal(1, blobCount setup)
            Assert.Contains("2 unreferenced content blob(s)", result.Output))

module ``Live mirror unaffected by pruning`` =
    [<Fact>]
    let ``Pruning with current files present`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let before = mirrorContent setup
            let result = prune setup [ "--yes" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal(before, mirrorContent setup)
            // The history still shows the file's current state: its row was not removed with its snapshot's neighbours.
            Assert.Equal(1, (rowsOf setup).Length))

module ``No retention policy configured`` =
    [<Fact>]
    let ``Prune without a configured policy`` () =
        scratch (fun setup ->
            // The scratch profile has no retention policy.
            write setup "a.txt" "first content"
            backupOnce setup
            changeFile setup "a.txt" "second content, longer"
            backupOnce setup
            let result = prune setup [ "--yes" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("no retention policy configured", result.Errors)
            Assert.Equal(2, (snapshotIds setup).Length)
            Assert.Equal(2, blobCount setup))

module ``Retention policy configuration`` =
    [<Fact>]
    let ``Profile without a retention policy`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            // A command that depends on retention reports the missing policy and does not assume one.
            let result = prune setup []
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("Profile 'docs' has no retention policy configured", result.Errors.Replace("\n", "")))

module ``Concurrent run prevention`` =
    [<Fact>]
    let ``Prune started while a run is already in progress for the same target`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let target = (AbsolutePath.tryCreate setup.Target).Value
            use _held = (RunLock.tryAcquire "other" target |> Result.defaultWith (fun _ -> failwith "no lock"))
            let result = prune setup [ "--yes" ]
            Assert.Equal(1, result.ExitCode)
            // The target is busy; the message does not say that the requesting profile is the one running.
            let message = result.Errors.Replace("\n", "")
            Assert.Contains("Another operation is already using target", message)
            Assert.Contains("a prune run for profile 'docs' cannot start", message)
            Assert.Equal(3, (snapshotIds setup).Length))

    [<Fact>]
    let ``Lock file inaccessible due to filesystem permissions`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let lockFile = Path.Combine(setup.Target, ".vara", "run.lock")
            File.WriteAllText(lockFile, "")
            denyAccess lockFile

            try
                let result = prune setup [ "--yes" ]
                Assert.Equal(1, result.ExitCode)
                let message = result.Errors.Replace("\n", "")
                Assert.Contains("permission problem", message)
                Assert.DoesNotContain("already using", message)
                Assert.Equal(3, (snapshotIds setup).Length)
            finally
                restoreAccess lockFile)

module ``Prune progress indication`` =
    [<Fact>]
    let ``Removing unreferenced blobs`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let result = prune setup [ "--yes" ]
            // Redirected output: appended lines with the count of blobs removed against the total, ending at all of them.
            Assert.Contains("Files 0 / 2", result.Output)
            Assert.Contains("Files 2 / 2 (100.0%)", result.Output))

    [<Fact>]
    let ``No unreferenced content to remove`` () =
        scratch (fun setup ->
            withPolicy setup
            write setup "a.txt" "alpha"
            // Two more runs change nothing, so their snapshots hold no rows. The first snapshot is eligible as well, but
            // it holds the current row of a.txt and stays; the empty one goes, and releases no content.
            backupOnce setup
            backupOnce setup
            backupOnce setup
            let result = prune setup [ "--yes" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Removed 1 snapshot(s) and 0 unreferenced content blob(s)", result.Output)
            // No progress for zero items.
            Assert.DoesNotContain("Files ", result.Output))

module ``Graceful cancellation via CtrlC`` =
    [<Fact>]
    let ``Single Ctrl+C before retention evaluation and snapshot removal have started`` () =
        scratch (fun setup ->
            threeSnapshots setup
            use source = new CancellationTokenSource()
            source.Cancel()

            let result =
                invokeEnv notAsking source.Token [| "prune"; "docs"; "--config"; setup.Config; "--yes" |]

            // No snapshot removed and no blob collected; reported as a stop, and not as a hard error. The C# build's
            // partial-failure code is kept: the spec's "exits successfully" and the build differ, a gap recorded in
            // the design (D7).
            Assert.Equal(2, result.ExitCode)
            Assert.Contains("Prune cancelled: stopped early", result.Output)
            Assert.Equal(3, (snapshotIds setup).Length)
            Assert.Equal(3, blobCount setup))

    [<Fact>]
    let ``Second Ctrl+C forces immediate termination`` () =
        scratch (fun setup ->
            threeSnapshots setup
            let forced = ref None
            let cancellation = GracefulCancellation(fun code -> forced.Value <- Some code)
            // The first Ctrl+C asks for a stop, which the prune honours before it starts.
            Assert.True(cancellation.RequestCancellation())
            let result = invokeEnv notAsking cancellation.Token [| "prune"; "docs"; "--config"; setup.Config; "--yes" |]
            Assert.Equal(2, result.ExitCode)
            // The second one does not wait for anything: it exits at once with the hard-error code.
            Assert.False(cancellation.RequestCancellation())
            Assert.Equal(Some(Outcome.exitCode Outcome.HardError), forced.Value))

    [<Fact>]
    let ``a stop during garbage collection is reported with what was removed`` () =
        // The run keeps what it had removed; the report says so (the engine test stops a real run mid-collection).
        let text = PruneView.summary 2 1 [] true |> Display.plainLines |> String.concat "\n"
        Assert.Contains("Removed 2 snapshot(s) and 1 unreferenced content blob(s)", text)
        Assert.Contains("Prune cancelled: stopped early", text)
