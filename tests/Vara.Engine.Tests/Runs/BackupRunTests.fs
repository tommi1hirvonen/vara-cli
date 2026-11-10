/// Scenario tests for the composed backup run and the dry-run plan.
module Vara.Engine.Tests.BackupRunTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open Vara.Domain
open Vara.Engine
open Vara.Engine.Tests

type Setup =
    { Root: string
      SourceDir: string
      TargetDir: string }

let absolute (path: string) : AbsolutePath = (AbsolutePath.tryCreate path).Value

let profileOf (setup: Setup) (name: string) (concurrency: Concurrency option) : Profile =
    match
        Profile.create
            name
            (absolute setup.TargetDir)
            [ { Path = absolute setup.SourceDir
                Recursive = true
                Excludes = []
                IncludeGlobs = []
                ExcludeGlobs = [] } ]
            None
            concurrency
    with
    | Ok profile -> profile
    | Error _ -> failwith "invalid test profile"

let within (body: Setup -> unit) =
    TempDir.using (fun root ->
        let setup =
            { Root = root
              SourceDir = Path.Combine(root, "source")
              TargetDir = Path.Combine(root, "target") }

        Directory.CreateDirectory setup.SourceDir |> ignore
        body setup)

let write (setup: Setup) (name: string) (text: string) : string =
    let path = Path.Combine(setup.SourceDir, name)
    TempDir.write path text
    path

let mirrorKey (setup: Setup) (name: string) : string =
    ScanRules.toMirrorPath (Path.Combine(setup.SourceDir, name)) |> ValueOption.get |> MirrorPath.value

let mirrorFile (setup: Setup) (name: string) : string =
    Path.Combine(setup.TargetDir, mirrorKey setup name)

let quiet = BackupRun.live (fun () -> false)

let runWith (env: BackupEnv) (profile: Profile) : Result<RunResult, BackupRefusal> =
    (BackupRun.run env profile).GetAwaiter().GetResult()

let runOk (env: BackupEnv) (profile: Profile) : RunResult =
    match runWith env profile with
    | Ok result -> result
    | Error refusal -> failwith (BackupRefusal.render refusal)

let query (setup: Setup) (sql: string) : string list =
    let database = Path.Combine(setup.TargetDir, ".vara", "profile.db")
    use connection = new SqliteConnection $"Data Source={database};Pooling=False"
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    use reader = command.ExecuteReader()
    let rows = ResizeArray()

    while reader.Read() do
        rows.Add(String.Join("|", [ for i in 0 .. reader.FieldCount - 1 -> string (reader.GetValue i) ]))

    List.ofSeq rows

let statuses (setup: Setup) : string list = query setup "SELECT status FROM snapshots ORDER BY id"

let denyAccess (path: string) =
    let info = ProcessStartInfo("cmd.exe", $"/c icacls \"{path}\" /deny \"%%USERNAME%%:(R,W)\"")
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use proc = Process.Start info |> nonNull
    proc.WaitForExit()
    Assert.Equal(0, proc.ExitCode)

let restoreAccess (path: string) =
    let info = ProcessStartInfo("cmd.exe", $"/c icacls \"{path}\" /remove:d \"%%USERNAME%%\"")
    info.RedirectStandardOutput <- true
    info.UseShellExecute <- false
    use proc = Process.Start info |> nonNull
    proc.WaitForExit()

module ``Backup run`` =
    [<Fact>]
    let ``A first backup mirrors the source and a second finds nothing to do`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            write setup "sub\\b.txt" "beta" |> ignore
            let profile = profileOf setup "p" None
            let first = runOk quiet profile
            Assert.Equal(SnapshotStatus.Complete, first.Status)
            Assert.Equal(2, first.Totals.FilesAdded)
            Assert.Equal("alpha", File.ReadAllText(mirrorFile setup "a.txt"))
            Assert.Equal("beta", File.ReadAllText(mirrorFile setup "sub\\b.txt"))
            Assert.Equal(Outcome.Success, Run.outcome first)

            let second = runOk quiet profile
            Assert.Equal(0, second.Totals.FilesAdded + second.Totals.FilesChanged + second.Totals.FilesMoved + second.Totals.FilesDeleted)
            Assert.Equal<string list>([ "Complete"; "Complete" ], statuses setup))

    [<Fact>]
    let ``Changes, moves and deletions are recorded and mirrored`` () =
        within (fun setup ->
            let changed = write setup "changed.txt" "before"
            let moved = write setup "moved.txt" "moving content"
            let deleted = write setup "deleted.txt" "doomed"
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore

            File.WriteAllText(changed, "after, longer")
            File.Move(moved, Path.Combine(setup.SourceDir, "renamed.txt"))
            File.Delete deleted
            write setup "added.txt" "new" |> ignore

            let result = runOk quiet profile
            Assert.Equal(1, result.Totals.FilesAdded)
            Assert.Equal(1, result.Totals.FilesChanged)
            Assert.Equal(1, result.Totals.FilesMoved)
            Assert.Equal(1, result.Totals.FilesDeleted)
            Assert.Equal("after, longer", File.ReadAllText(mirrorFile setup "changed.txt"))
            Assert.Equal("moving content", File.ReadAllText(mirrorFile setup "renamed.txt"))
            Assert.False(File.Exists(mirrorFile setup "moved.txt"))
            Assert.False(File.Exists(mirrorFile setup "deleted.txt"))
            // Every content ever stored stays in the version store: before, after, moved, deleted and added.
            let blobs = Directory.EnumerateFiles(Path.Combine(setup.TargetDir, ".vara", "versions"), "*", SearchOption.AllDirectories)
            Assert.Equal(5, Seq.length blobs))

    [<Fact>]
    let ``Failed paths give a partial failure`` () =
        within (fun setup ->
            let locked = write setup "locked.txt" "locked"
            write setup "fine.txt" "fine" |> ignore
            use hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)
            let result = runOk quiet (profileOf setup "p" None)
            Assert.Equal(1, result.Totals.FilesAdded)
            Assert.Equal(1, result.Totals.FilesFailed)
            Assert.Equal<string list>([ mirrorKey setup "locked.txt" ], result.FailedPaths)
            Assert.Equal(Outcome.PartialFailure, Run.outcome result))

module ``Concurrent run prevention`` =
    [<Fact>]
    let ``Second run started while one is in progress`` () =
        within (fun setup ->
            write setup "a.txt" "a" |> ignore
            let profile = profileOf setup "p" None
            use _held = RunLock.tryAcquire "p" (absolute setup.TargetDir) |> Result.defaultWith (fun _ -> failwith "no lock")

            match runWith quiet profile with
            | Error(BackupRefusal.TargetBusy(target, name)) ->
                Assert.Equal(absolute setup.TargetDir, target)
                Assert.Equal("p", name)
            | _ -> Assert.Fail "expected the target to be busy"

            // Nothing was recorded or written by the refused run.
            Assert.False(File.Exists(Path.Combine(setup.TargetDir, ".vara", "profile.db"))))

    [<Fact>]
    let ``Second run started for a different profile sharing the same target`` () =
        within (fun setup ->
            let first = profileOf setup "first" None
            let second = profileOf setup "second" None
            use _held = RunLock.tryAcquire "first" (absolute setup.TargetDir) |> Result.defaultWith (fun _ -> failwith "no lock")

            match runWith quiet second with
            | Error refusal ->
                let message = BackupRefusal.render refusal
                // The target is busy; the message does not claim the second profile is the one running.
                Assert.Contains("Another operation is already using target", message)
                Assert.DoesNotContain("is already running", message)
                Assert.DoesNotContain("first", message)
            | Ok _ -> Assert.Fail "expected a refusal"

            ignore first)

    [<Fact>]
    let ``Only a handle held elsewhere makes the target busy`` () =
        // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION, as the file system reports them.
        Assert.True(RunLock.isHeldElsewhere (IOException("in use", 0x80070020)))
        Assert.True(RunLock.isHeldElsewhere (IOException("locked", 0x80070021)))
        // ERROR_DISK_FULL and ERROR_PATH_NOT_FOUND are failures of their own, not a busy target.
        Assert.False(RunLock.isHeldElsewhere (IOException("full", 0x80070070)))
        Assert.False(RunLock.isHeldElsewhere (IOException("gone", 0x80070003)))

    [<Fact>]
    let ``Lock file inaccessible due to filesystem permissions`` () =
        within (fun setup ->
            let profile = profileOf setup "p" None
            let lockFile = Path.Combine(setup.TargetDir, ".vara", "run.lock")
            Directory.CreateDirectory(Path.GetDirectoryName lockFile |> nonNull) |> ignore
            File.WriteAllText(lockFile, "")
            denyAccess lockFile

            try
                match runWith quiet profile with
                | Error(BackupRefusal.RunLockAccessDenied target) ->
                    Assert.Equal(absolute setup.TargetDir, target)
                    Assert.Contains("permission problem", BackupRefusal.render (BackupRefusal.RunLockAccessDenied target))
                | _ -> Assert.Fail "expected a lock access refusal"
            finally
                restoreAccess lockFile)

module ``Original failure cause is preserved when failure recording itself fails`` =
    [<Fact>]
    let ``Recording a run's failure state itself fails`` () =
        within (fun setup ->
            let original = write setup "a.txt" "same size!!"
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore

            // A moved file makes planning read its content; that read crashes, and from then on the clock fails, so
            // recording the failed snapshot fails too.
            File.Move(original, Path.Combine(setup.SourceDir, "b.txt"))
            let broken = ref false
            let diagnostics = ResizeArray<string>()

            let env =
                { quiet with
                    OpenRead =
                        fun _ ->
                            broken.Value <- true
                            raise (InvalidOperationException "the original failure")
                    Clock =
                        fun () ->
                            if broken.Value then
                                raise (InvalidOperationException "recording failed")

                            DateTimeOffset.UtcNow
                    Diagnostics = diagnostics.Add }

            let thrown = Assert.ThrowsAny<Exception>(fun () -> runWith env profile |> ignore)

            let rec messages (e: exn) : string list =
                match e with
                | :? AggregateException as aggregate -> aggregate.InnerExceptions |> Seq.toList |> List.collect messages
                | e -> [ e.Message ]

            Assert.Equal<string list>([ "the original failure" ], messages thrown)
            Assert.Contains(diagnostics, fun m -> m.Contains "recording failed"))

module ``Crash and interruption safety`` =
    [<Fact>]
    let ``Run interrupted mid-way`` () =
        within (fun setup ->
            write setup "a.txt" "alpha content" |> ignore
            let profile = profileOf setup "p" None

            // A run that died leaves its snapshot Running; the next run records it as failed and carries on.
            let dead = SqliteManifest.openAt (Path.Combine(setup.TargetDir, ".vara", "profile.db")) true
            SqliteManifest.beginSnapshot dead DateTimeOffset.UtcNow |> ignore
            SqliteManifest.close dead

            let result = runOk quiet profile
            Assert.Equal<string list>([ "Failed"; "Complete" ], statuses setup)
            Assert.Equal(1, result.Totals.FilesAdded)
            // Every mirror file holds complete content.
            Assert.Equal("alpha content", File.ReadAllText(mirrorFile setup "a.txt"))
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(setup.TargetDir, ".vara", "tmp"))))

    [<Fact>]
    let ``A crashed run is recorded as failed and the crash is rethrown`` () =
        within (fun setup ->
            for i in 1..3 do
                write setup $"f{i}.txt" $"content {i}" |> ignore

            let opened = ref 0

            let env =
                { quiet with
                    OpenRead =
                        fun path ->
                            if Interlocked.Increment(&opened.contents) = 2 then
                                raise (InvalidOperationException "crash")

                            File.OpenRead path :> Stream }

            let thrown = Assert.Throws<AggregateException>(fun () -> runWith env (profileOf setup "p" None) |> ignore)
            Assert.Equal("crash", Assert.Single(thrown.InnerExceptions).Message)
            Assert.Equal<string list>([ "Failed" ], statuses setup))

module ``Graceful cancellation via CtrlC`` =
    [<Fact>]
    let ``Ctrl+C pressed before any file has been transferred`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            let result = runOk (BackupRun.live (fun () -> true)) (profileOf setup "p" None)
            Assert.Equal(SnapshotStatus.Cancelled, result.Status)
            Assert.Equal(0, result.Totals.FilesAdded)
            Assert.Equal(Outcome.PartialFailure, Run.outcome result)
            Assert.Equal<string list>([ "Cancelled" ], statuses setup)
            Assert.False(File.Exists(mirrorFile setup "a.txt")))

module ``Dry-run mode reports planned changes without executing them`` =
    [<Fact>]
    let ``Dry run reports planned changes without writing anything`` () =
        within (fun setup ->
            let changed = write setup "changed.txt" "before"
            let deleted = write setup "deleted.txt" "doomed"
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore

            File.WriteAllText(changed, "after, longer")
            File.Delete deleted
            write setup "added.txt" "new file" |> ignore

            let rows = query setup "SELECT COUNT(*) FROM file_versions"
            let mirrorBefore = File.ReadAllText(mirrorFile setup "changed.txt")

            match BackupRun.dryRun quiet profile with
            | Ok summary ->
                Assert.Equal(1, summary.Added)
                Assert.Equal(1, summary.Changed)
                Assert.Equal(1, summary.Deleted)
                Assert.Equal(0, summary.Moved)
                Assert.Equal(int64 ("after, longer".Length + "new file".Length), summary.PlannedBytes)
            | Error _ -> Assert.Fail "expected a summary"

            // Mirror, store and manifest are as they were, and no snapshot was created.
            Assert.Equal(mirrorBefore, File.ReadAllText(mirrorFile setup "changed.txt"))
            Assert.True(File.Exists(mirrorFile setup "deleted.txt"))
            Assert.Equal<string list>(rows, query setup "SELECT COUNT(*) FROM file_versions")
            Assert.Equal<string list>([ "Complete" ], statuses setup))

    [<Fact>]
    let ``A dry run of a profile that never ran creates no manifest`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore

            match BackupRun.dryRun quiet (profileOf setup "p" None) with
            | Ok summary -> Assert.Equal(1, summary.Added)
            | Error _ -> Assert.Fail "expected a summary"

            Assert.False(File.Exists(Path.Combine(setup.TargetDir, ".vara", "profile.db")))
            Assert.False(Directory.Exists(Path.Combine(setup.TargetDir, ".vara", "versions")))
            Assert.False(File.Exists(mirrorFile setup "a.txt")))

    [<Fact>]
    let ``Dry run refuses to start while a real run is in progress for the same target`` () =
        within (fun setup ->
            use _held = RunLock.tryAcquire "other" (absolute setup.TargetDir) |> Result.defaultWith (fun _ -> failwith "no lock")

            match BackupRun.dryRun quiet (profileOf setup "p" None) with
            | Error(BackupRefusal.TargetBusy _) -> ()
            | _ -> Assert.Fail "expected the target to be busy")

    [<Fact>]
    let ``Dry run reports unreadable paths without treating them as an error`` () =
        within (fun setup ->
            write setup "keep.txt" "k" |> ignore
            let denied = Path.Combine(setup.SourceDir, "denied-dir")
            TempDir.write (Path.Combine(denied, "hidden.txt")) "h"
            let info = ProcessStartInfo("cmd.exe", $"/c icacls \"{denied}\" /deny \"%%USERNAME%%:(RD)\"")
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            use proc = Process.Start info |> nonNull
            proc.WaitForExit()

            try
                match BackupRun.dryRun quiet (profileOf setup "p" None) with
                | Ok summary ->
                    Assert.Equal<string list>([ "denied-dir" ], summary.Unscanned)
                    Assert.Equal(1, summary.Added)
                | Error _ -> Assert.Fail "expected a summary"
            finally
                restoreAccess denied)

    [<Fact>]
    let ``No changes pending`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore

            match BackupRun.dryRun quiet profile with
            | Ok summary ->
                Assert.Equal(0, summary.Added + summary.Changed + summary.Moved + summary.Deleted)
                Assert.Equal(0L, summary.PlannedBytes)
            | Error _ -> Assert.Fail "expected a summary")

module ``Configurable backup-stage concurrency`` =
    /// Runs the profile with a slow, observed read and returns the most reads that were open at once.
    let private peakReads (_: Setup) (profile: Profile) : int =
        let active = ref 0
        let peak = ref 0

        let slowOpen (path: string) : Stream =
            let now = Interlocked.Increment(&active.contents)
            lock peak (fun () -> peak.Value <- max peak.Value now)
            Thread.Sleep 60
            Interlocked.Decrement(&active.contents) |> ignore
            File.OpenRead path :> Stream

        runOk { quiet with OpenRead = slowOpen } profile |> ignore
        peak.Value

    let private limited (scan: int option) (transfer: int option) : Concurrency option =
        Some
            { Scan = scan |> Option.bind ConcurrencyLimit.tryCreate
              Transfer = transfer |> Option.bind ConcurrencyLimit.tryCreate }

    let private files (setup: Setup) (count: int) =
        for i in 1..count do
            write setup $"f{i}.txt" $"content {i}" |> ignore

    [<Fact>]
    let ``Default transfer concurrency serializes transfers`` () =
        within (fun setup ->
            files setup 6
            Assert.Equal(1, peakReads setup (profileOf setup "p" None)))

    [<Fact>]
    let ``Configured transfer concurrency overrides the default`` () =
        within (fun setup ->
            files setup 8
            let peak = peakReads setup (profileOf setup "p" (limited None (Some 4)))
            Assert.InRange(peak, 2, 4))

    /// Planning reads only same-size candidates of deleted files, so each of these files is a move.
    let private moves (setup: Setup) (count: int) =
        for i in 1..count do
            write setup $"old{i}.txt" $"content {i:D3}" |> ignore

        runOk quiet (profileOf setup "p" None) |> ignore

        for i in 1..count do
            File.Move(Path.Combine(setup.SourceDir, $"old{i}.txt"), Path.Combine(setup.SourceDir, $"new{i}.txt"))

    [<Fact>]
    let ``Default scan concurrency scales with available processors`` () =
        if Environment.ProcessorCount > 1 then
            within (fun setup ->
                moves setup 8
                Assert.True(peakReads setup (profileOf setup "p" None) > 1))

    [<Fact>]
    let ``Configured scan concurrency overrides the default`` () =
        within (fun setup ->
            moves setup 6
            Assert.Equal(1, peakReads setup (profileOf setup "p" (limited (Some 1) None))))

/// What a progress display needs from the engine beyond the per-file events (fsharp-backup-presentation, design D7).
module ``Run observers`` =
    let private watching (totals: ResizeArray<PlanTotals>) (counts: ResizeArray<int>) : BackupEnv =
        { quiet with
            Observers =
                { Observers.none with
                    ScanProgress = ValueSome counts.Add
                    TransferPhaseStarting = totals.Add } }

    [<Fact>]
    let ``The transfer phase starts with the plan's totals`` () =
        within (fun setup ->
            write setup "a.txt" "12345" |> ignore
            write setup "b.txt" "123" |> ignore
            let totals = ResizeArray()
            runOk (watching totals (ResizeArray())) (profileOf setup "p" None) |> ignore
            let only = Assert.Single totals
            Assert.Equal(8L, only.Bytes)
            Assert.Equal(2, only.Files)

            // Nothing changed: the phase still starts once, with nothing to transfer.
            let again = ResizeArray()
            runOk (watching again (ResizeArray())) (profileOf setup "p" None) |> ignore
            let none = Assert.Single again
            Assert.Equal(0L, none.Bytes)
            Assert.Equal(0, none.Files))

    [<Fact>]
    let ``The scan reports its running entry count for a real run and a dry run`` () =
        within (fun setup ->
            let total = BackupRun.scanReportInterval * 2 + 5

            for index in 1..total do
                write setup $"f{index}.txt" "x" |> ignore

            let profile = profileOf setup "p" None
            let counts = ResizeArray()
            runOk (watching (ResizeArray()) counts) profile |> ignore
            // A report per interval, counting upward; the entries past the last full interval are not reported.
            Assert.Equal<int list>([ BackupRun.scanReportInterval; BackupRun.scanReportInterval * 2 ], List.ofSeq counts)

            let dryCounts = ResizeArray()

            match BackupRun.dryRun (watching (ResizeArray()) dryCounts) profile with
            | Ok _ -> ()
            | Error refusal -> failwith (BackupRefusal.render refusal)

            Assert.Equal<int list>([ BackupRun.scanReportInterval; BackupRun.scanReportInterval * 2 ], List.ofSeq dryCounts))

module ``Incremental change detection`` =
    [<Theory>]
    [<InlineData("äiti.txt", "Äiti.txt")>]
    [<InlineData("Äiti.txt", "äiti.txt")>]
    let ``File with a non-ASCII name changes casing and content`` (before: string, after: string) =
        within (fun setup ->
            write setup before "first content" |> ignore
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore

            File.Move(Path.Combine(setup.SourceDir, before), Path.Combine(setup.SourceDir, after))
            File.WriteAllText(Path.Combine(setup.SourceDir, after), "second content, longer")

            let first = runOk quiet profile
            Assert.Equal(1, first.Totals.FilesChanged)
            Assert.Equal(0, first.Totals.FilesAdded + first.Totals.FilesMoved + first.Totals.FilesDeleted)

            let second = runOk quiet profile
            Assert.Equal(0, second.Totals.FilesAdded + second.Totals.FilesChanged + second.Totals.FilesMoved + second.Totals.FilesDeleted)
            Assert.Equal(0L, second.Totals.BytesTransferred)

            let handle = SqliteManifest.openReadOnly (Path.Combine(setup.TargetDir, ".vara", "profile.db"))

            try
                let state = SqliteManifest.currentState handle
                Assert.Equal(1, state.Count)
                Assert.Equal<string list>([ mirrorKey setup after ], List.ofSeq state.Keys)
            finally
                SqliteManifest.close handle)
