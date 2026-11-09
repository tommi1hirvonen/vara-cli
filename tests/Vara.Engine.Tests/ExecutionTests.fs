/// Scenario tests for the execution shell: metadata operations, pooled transfers, and the single recorder.
module Vara.Engine.Tests.ExecutionTests

open System
open System.Collections.Generic
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open Vara.Domain
open Vara.Engine
open Vara.Engine.Tests

/// A target and a source under one scratch directory, with a manifest and a running snapshot.
type private Rig =
    { Root: string
      Source: string
      Layout: StoreLayout
      Handle: ManifestHandle
      Snapshot: int64
      Batch: ManifestBatch }

let private openRig (root: string) : Rig =
    let target = Path.Combine(root, "target")
    let layout = ContentStore.layout target
    ContentStore.ensureDirectories layout
    let handle = SqliteManifest.openAt (Path.Combine(target, ".vara", "profile.db")) true
    let snapshot = SqliteManifest.beginSnapshot handle DateTimeOffset.UtcNow

    { Root = root
      Source = Path.Combine(root, "source")
      Layout = layout
      Handle = handle
      Snapshot = snapshot
      Batch = SqliteManifest.beginBatch handle }

let private defaultEnv (rig: Rig) : ExecutionEnv =
    { Store = rig.Layout
      Links = ContentStore.realLinks
      SupportsHardlinks = ContentStore.probeHardlinkSupport ContentStore.realLinks rig.Layout
      OpenRead = Signatures.openFile
      Now = fun () -> DateTimeOffset.UtcNow
      CheckpointInterval = TimeSpan.FromSeconds 30.0
      TransferConcurrency = 1
      Observers = Observers.none
      ShouldStop = fun () -> false }

let private sourceFile (rig: Rig) (name: string) (text: string) : string =
    let path = Path.Combine(rig.Source, name)
    TempDir.write path text
    path

let private mirrorKey (rig: Rig) (name: string) : string =
    ScanRules.toMirrorPath (Path.Combine(rig.Source, name)) |> ValueOption.get |> MirrorPath.value

let private mirrorFile (rig: Rig) (name: string) : string =
    Path.Combine(rig.Layout.MirrorRoot, mirrorKey rig name)

/// Scan, diff and plan against the manifest as it stands, the way the run composition will.
let private planFor (rig: Rig) : BackupPlan * ScanFailure list =
    let tracked = SqliteManifest.currentState rig.Handle
    let diff = Diff.run tracked (Scanner.scan [ ScannerTests.source rig.Source ])
    let candidates = Planning.moveCandidates diff.Deleted
    let adds = Planning.sizeMatchedAdds candidates diff.Pending
    let hashes = Signatures.readFullHashes Signatures.openFile 2 candidates adds
    Planning.plan tracked diff candidates hashes, diff.Failures

let private execute (env: ExecutionEnv) (rig: Rig) (plan: BackupPlan) : ExecutionEnd =
    (Execution.run env rig.Snapshot DateTimeOffset.UtcNow rig.Batch plan).GetAwaiter().GetResult()

/// Ends the run the way the pipeline does, then drops the transaction: everything checkpointed stays.
let private finish (rig: Rig) (ended: ExecutionEnd) : unit =
    SqliteManifest.finishSnapshot ended.Batch rig.Snapshot SnapshotStatus.Complete DateTimeOffset.UtcNow ended.Run.Counts
    SqliteManifest.checkpoint ended.Batch |> SqliteManifest.discard

let private persistedRows (rig: Rig) : int =
    let database = Path.Combine(rig.Layout.MirrorRoot, ".vara", "profile.db")
    use connection = new SqliteConnection($"Data Source={database};Pooling=False")
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT COUNT(*) FROM file_versions"
    Convert.ToInt32(command.ExecuteScalar())

let private within (body: Rig -> unit) =
    TempDir.using (fun root ->
        let rig = openRig root

        try
            body rig
        finally
            SqliteManifest.close rig.Handle)

module ``File modified during its own transfer is not recorded under stale metadata`` =
    /// Opens the file for the engine and then changes its modification time, as another process would.
    let private touchingOpen (path: string) : Stream =
        let stream = File.OpenRead path
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours 1.0)
        stream :> Stream

    [<Fact>]
    let ``File unchanged between scan and transfer`` () =
        within (fun rig ->
            let path = sourceFile rig "a.txt" "content"
            let scannedAt = DateTimeOffset(FileInfo(path).LastWriteTimeUtc)
            let plan, _ = planFor rig
            let ended = execute (defaultEnv rig) rig plan
            Assert.True ended.Failure.IsNone
            Assert.Equal(1, ended.Run.Counts.FilesAdded)
            finish rig ended
            // The entry carries the scan-time modification time.
            match (SqliteManifest.currentState rig.Handle)[mirrorKey rig "a.txt"] with
            | TrackedState.Regular content -> Assert.True(content.SourceModifiedAt.Equals scannedAt)
            | _ -> Assert.Fail "expected regular content")

    [<Fact>]
    let ``File modified after being scanned but before its content is read`` () =
        within (fun rig ->
            sourceFile rig "a.txt" "content" |> ignore
            let plan, _ = planFor rig
            let ended = execute { defaultEnv rig with OpenRead = touchingOpen } rig plan
            Assert.Equal(1, ended.Run.Counts.FilesFailed)
            Assert.Equal(0, ended.Run.Counts.FilesAdded)
            Assert.Equal<string list>([ mirrorKey rig "a.txt" ], ended.Run.FailedPaths |> List.map MirrorPath.value)
            // Nothing is placed and no row is written for the path.
            Assert.False(File.Exists(mirrorFile rig "a.txt"))
            finish rig ended
            Assert.False((SqliteManifest.currentState rig.Handle).ContainsKey(mirrorKey rig "a.txt")))

    [<Fact>]
    let ``File repeatedly modified during a large backup run`` () =
        within (fun rig ->
            let path = sourceFile rig "a.txt" "content"
            let plan, _ = planFor rig
            finish rig (execute (defaultEnv rig) rig plan)

            // Modified after a successful run, then a torn transfer: the manifest still holds the old state.
            File.WriteAllText(path, "changed content")
            let rig = { rig with Batch = SqliteManifest.beginBatch rig.Handle }
            let torn, _ = planFor rig
            let ended = execute { defaultEnv rig with OpenRead = touchingOpen } rig torn
            Assert.Equal(1, ended.Run.Counts.FilesFailed)
            finish rig ended

            // The next scan still selects the file, because its manifest state never advanced.
            let again, _ = planFor rig
            Assert.Equal(1, List.length again.Transfers))

module ``Unreadable files do not abort the run`` =
    [<Fact>]
    let ``Locked file encountered`` () =
        within (fun rig ->
            let locked = sourceFile rig "locked.txt" "locked"
            sourceFile rig "fine.txt" "fine" |> ignore
            let plan, _ = planFor rig
            use hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)
            let ended = execute (defaultEnv rig) rig plan
            Assert.True ended.Failure.IsNone
            Assert.Equal(1, ended.Run.Counts.FilesAdded)
            Assert.Equal(1, ended.Run.Counts.FilesFailed)
            Assert.Equal<string list>([ mirrorKey rig "locked.txt" ], ended.Run.FailedPaths |> List.map MirrorPath.value)
            Assert.True(File.Exists(mirrorFile rig "fine.txt")))

    [<Fact>]
    let ``Locked mirror entry encountered during move`` () =
        within (fun rig ->
            let old = sourceFile rig "old.txt" "moving"
            finish rig (execute (defaultEnv rig) rig (fst (planFor rig)))
            let rig = { rig with Batch = SqliteManifest.beginBatch rig.Handle }
            File.Move(old, Path.Combine(rig.Source, "new.txt"))
            sourceFile rig "other.txt" "other" |> ignore
            let plan, _ = planFor rig
            Assert.Equal(1, plan.Metadata |> List.filter (function MetadataOperation.Move _ -> true | _ -> false) |> List.length)
            use hold = new FileStream(mirrorFile rig "old.txt", FileMode.Open, FileAccess.Read, FileShare.None)
            let ended = execute (defaultEnv rig) rig plan
            Assert.True ended.Failure.IsNone
            Assert.Equal(0, ended.Run.Counts.FilesMoved)
            Assert.Equal(1, ended.Run.Counts.FilesFailed)
            Assert.Equal(1, ended.Run.Counts.FilesAdded)) // the other file is still backed up

    [<Fact>]
    let ``Locked mirror entry encountered during delete`` () =
        within (fun rig ->
            let doomed = sourceFile rig "doomed.txt" "doomed"
            finish rig (execute (defaultEnv rig) rig (fst (planFor rig)))
            let rig = { rig with Batch = SqliteManifest.beginBatch rig.Handle }
            File.Delete doomed
            sourceFile rig "fine.txt" "fine" |> ignore
            let plan, _ = planFor rig
            use hold = new FileStream(mirrorFile rig "doomed.txt", FileMode.Open, FileAccess.Read, FileShare.None)
            let ended = execute (defaultEnv rig) rig plan
            Assert.True ended.Failure.IsNone
            Assert.Equal(0, ended.Run.Counts.FilesDeleted)
            Assert.Equal(1, ended.Run.Counts.FilesFailed)
            Assert.Equal(1, ended.Run.Counts.FilesAdded))

module ``Mirror writes are confined to the target root`` =
    [<Fact>]
    let ``A mirror path that escapes the root is a failed operation, not a crash`` () =
        within (fun rig ->
            let file = sourceFile rig "a.txt" "content"
            let info = FileInfo file

            let escaping =
                { Source =
                    { FilePath = MirrorPath.ofString "..\\..\\escaped.txt"
                      FileSource = SourcePath.ofString file
                      Size = info.Length
                      ModifiedAt = DateTimeOffset info.LastWriteTimeUtc }
                  TransferKind = TransferKind.Added }

            let plan = { Metadata = []; Transfers = [ escaping ]; TotalBytes = info.Length }
            let ended = execute (defaultEnv rig) rig plan
            Assert.True ended.Failure.IsNone
            Assert.Equal(1, ended.Run.Counts.FilesFailed)
            Assert.False(File.Exists(Path.Combine(rig.Root, "escaped.txt"))))

module ``Manifest writes are checkpointed periodically during a run`` =
    let private many (rig: Rig) (count: int) =
        for i in 1..count do
            sourceFile rig $"f{i:D3}.txt" $"content {i}" |> ignore

    /// A clock that moves one second per reading.
    let private ticking () : unit -> DateTimeOffset =
        let ticks = ref 0
        fun () ->
            ticks.Value <- ticks.Value + 1
            DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(float ticks.Value)

    [<Fact>]
    let ``Many small files backed up`` () =
        within (fun rig ->
            many rig 60
            let plan, _ = planFor rig
            // The default interval is 30 seconds: the whole run is one transaction, committed once at the end.
            let ended = execute (defaultEnv rig) rig plan
            Assert.Equal(60, ended.Run.Counts.FilesAdded)
            Assert.Equal(0, persistedRows rig) // no commit per file; nothing committed until the caller commits
            finish rig ended
            Assert.Equal(60, persistedRows rig))

    [<Fact>]
    let ``Long-running run interrupted near the end only loses recent progress`` () =
        within (fun rig ->
            many rig 42
            let plan, _ = planFor rig
            let ended = execute { defaultEnv rig with Now = ticking (); CheckpointInterval = TimeSpan.FromSeconds 5.0 } rig plan
            Assert.Equal(42, ended.Run.Counts.FilesAdded)
            // Interrupted: the open transaction is dropped without a final commit.
            SqliteManifest.discard ended.Batch
            let survived = persistedRows rig
            Assert.True(survived > 0 && survived < 42, $"{survived} of 42 rows survived the interruption"))

    [<Fact>]
    let ``Crash mid-checkpoint leaves mirror and manifest consistent`` () =
        within (fun rig ->
            many rig 5
            let plan, _ = planFor rig
            let ended = execute (defaultEnv rig) rig plan
            SqliteManifest.discard ended.Batch // the interruption: nothing was committed
            Assert.Equal(0, persistedRows rig)
            // The mirror content was written, but no partial row exists; a later run records the files without error.
            Assert.True(File.Exists(mirrorFile rig "f001.txt"))
            let rig = { rig with Batch = SqliteManifest.beginBatch rig.Handle }
            let again, _ = planFor rig
            Assert.Equal(5, List.length again.Transfers)
            let recovered = execute (defaultEnv rig) rig again
            Assert.Equal(5, recovered.Run.Counts.FilesAdded)
            Assert.Equal(0, recovered.Run.Counts.FilesFailed)
            finish rig recovered
            Assert.Equal(5, persistedRows rig))

    [<Fact>]
    let ``Crash between a move's mirror relocation and its checkpoint commit`` () =
        within (fun rig ->
            let old = sourceFile rig "old.txt" "moving content"
            finish rig (execute (defaultEnv rig) rig (fst (planFor rig)))
            let rig = { rig with Batch = SqliteManifest.beginBatch rig.Handle }
            File.Move(old, Path.Combine(rig.Source, "new.txt"))
            let plan, _ = planFor rig
            let interrupted = execute (defaultEnv rig) rig plan
            Assert.Equal(1, interrupted.Run.Counts.FilesMoved)
            SqliteManifest.discard interrupted.Batch // the mirror relocation happened, the manifest never saw it

            // A later run still sees the file at the old path and plans the move again; the relocation counts as done.
            let rig = { rig with Batch = SqliteManifest.beginBatch rig.Handle }
            let again, _ = planFor rig
            let recovered = execute (defaultEnv rig) rig again
            Assert.Equal(1, recovered.Run.Counts.FilesMoved)
            Assert.Equal(0, recovered.Run.Counts.FilesFailed)
            finish rig recovered
            Assert.True(File.Exists(mirrorFile rig "new.txt")))

// F# cannot spell `+` in a module name; the coverage check ignores it when it compares names.
module ``Graceful cancellation via CtrlC`` =
    [<Fact>]
    let ``Single Ctrl+C during an active transfer`` () =
        within (fun rig ->
            for i in 1..10 do
                sourceFile rig $"f{i}.txt" $"content {i}" |> ignore

            let plan, _ = planFor rig
            let stop = ref false

            // The stop arrives while the first file is being read: it finishes, and with one worker no other starts.
            let stoppingOpen (path: string) : Stream =
                stop.Value <- true
                File.OpenRead path :> Stream

            let ended = execute { defaultEnv rig with OpenRead = stoppingOpen; ShouldStop = fun () -> stop.Value } rig plan
            Assert.True ended.Failure.IsNone
            Assert.Equal(1, ended.Run.Counts.FilesAdded)
            // What finished is recorded and committed with the cancelled ending.
            SqliteManifest.finishSnapshot ended.Batch rig.Snapshot (Run.endStatus true) DateTimeOffset.UtcNow ended.Run.Counts
            SqliteManifest.checkpoint ended.Batch |> SqliteManifest.discard
            Assert.Equal(1, persistedRows rig))

    [<Fact>]
    let ``Cancelled run resumes like any interrupted run`` () =
        within (fun rig ->
            for i in 1..6 do
                sourceFile rig $"f{i}.txt" $"content {i}" |> ignore

            let stop = ref false

            let stoppingOpen (path: string) : Stream =
                stop.Value <- true
                File.OpenRead path :> Stream

            let cancelled = execute { defaultEnv rig with OpenRead = stoppingOpen; ShouldStop = fun () -> stop.Value } rig (fst (planFor rig))
            finish rig cancelled
            let rig = { rig with Batch = SqliteManifest.beginBatch rig.Handle }
            // Only what was not committed before the cancellation is detected again.
            let resumed, _ = planFor rig
            Assert.Equal(6 - cancelled.Run.Counts.FilesAdded, List.length resumed.Transfers))

    [<Fact>]
    let ``A stop requested before any operation starts runs nothing`` () =
        within (fun rig ->
            sourceFile rig "a.txt" "content" |> ignore
            let plan, _ = planFor rig
            let ended = execute { defaultEnv rig with ShouldStop = fun () -> true } rig plan
            Assert.Equal(0, ended.Run.Counts.FilesAdded + ended.Run.Counts.FilesFailed)
            Assert.False(File.Exists(mirrorFile rig "a.txt")))

module ``Failures of the run itself`` =
    [<Fact>]
    let ``A worker crash is reported after everything before it was recorded`` () =
        within (fun rig ->
            for i in 1..5 do
                sourceFile rig $"f{i}.txt" $"content {i}" |> ignore

            let plan, _ = planFor rig
            let opened = ref 0

            let crashingOpen (path: string) : Stream =
                // The third file read blows up with something that is not an I/O error.
                if Interlocked.Increment(&opened.contents) = 3 then
                    raise (InvalidOperationException "worker crash")

                File.OpenRead path :> Stream

            let ended = execute { defaultEnv rig with OpenRead = crashingOpen } rig plan

            match ended.Failure with
            | ValueSome failure ->
                let aggregate = Assert.IsType<AggregateException>(failure.SourceException)
                Assert.Equal("worker crash", Assert.Single(aggregate.InnerExceptions).Message)
            | ValueNone -> Assert.Fail "expected a failure"

            // The files finished before the crash are recorded, and the batch is still usable to end the snapshot.
            Assert.True(ended.Run.Counts.FilesAdded >= 1)
            finish rig ended
            Assert.Equal(ended.Run.Counts.FilesAdded, persistedRows rig))

    [<Fact>]
    let ``A failing manifest stops the pool and is kept as the failure`` () =
        within (fun rig ->
            for i in 1..5 do
                sourceFile rig $"f{i}.txt" $"content {i}" |> ignore

            let plan, _ = planFor rig
            let reads = ref 0

            let clock () : DateTimeOffset =
                if Interlocked.Increment(&reads.contents) > 3 then
                    raise (InvalidOperationException "manifest failure")

                DateTimeOffset.UtcNow

            let ended = execute { defaultEnv rig with Now = clock } rig plan

            match ended.Failure with
            | ValueSome failure -> Assert.Equal("manifest failure", failure.SourceException.Message)
            | ValueNone -> Assert.Fail "expected a failure"

            Assert.True(ended.Run.Counts.FilesAdded < 5))
