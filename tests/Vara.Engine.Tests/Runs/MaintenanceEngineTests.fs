/// Tests of the effects of `prune` and `check` against a real target written by the F# backup: the manifest queries and
/// the writer, the garbage collection and the verification. Spec-named modules hold the scenarios that belong to the
/// effects; the others say what they check.
module Vara.Engine.Tests.MaintenanceEngineTests

open System
open System.IO
open System.Security.Cryptography
open Xunit
open Vara.Domain
open Vara.Engine
open Vara.Engine.Tests
open Vara.Engine.Tests.BackupRunTests

let private databaseOf (setup: Setup) : string = Path.Combine(setup.TargetDir, ".vara", "profile.db")
let private store (setup: Setup) : StoreLayout = ContentStore.layout setup.TargetDir

/// Keeps only the newest completed snapshot, so that every older one is eligible.
let private keepNothing: RetentionPolicy =
    { KeepDaily = RetentionCount.zero
      KeepWeekly = RetentionCount.zero
      KeepMonthly = RetentionCount.zero
      KeepYearly = RetentionCount.zero }

let private reader (setup: Setup) (body: ManifestHandle -> 'a) : 'a =
    let handle = SqliteManifest.openReadOnly (databaseOf setup)

    try
        body handle
    finally
        SqliteManifest.close handle

let private writer (setup: Setup) (body: ManifestHandle -> 'a) : 'a =
    let handle = SqliteManifest.openAt (databaseOf setup) false

    try
        body handle
    finally
        SqliteManifest.close handle

/// The content hash of the latest version of a source file.
let private hashOf (setup: Setup) (name: string) : ContentHash =
    let rows = query setup $"SELECT content_hash FROM file_versions WHERE relative_path = '{mirrorKey setup name}' ORDER BY id DESC LIMIT 1"
    ContentHash.tryCreate rows.Head |> ValueOption.get

let private hash (text: string) : ContentHash = (ContentHash.tryCreate text).Value

/// The version of a source file as it was first recorded.
let private firstHashOf (setup: Setup) (name: string) : ContentHash =
    let rows = query setup $"SELECT content_hash FROM file_versions WHERE relative_path = '{mirrorKey setup name}' ORDER BY id LIMIT 1"
    ContentHash.tryCreate rows.Head |> ValueOption.get

let private blobOf (setup: Setup) (hash: ContentHash) : string = ContentStore.blobPath (store setup) hash

let private snapshotIds (setup: Setup) : int64 list =
    reader setup (fun handle -> ManifestQueries.snapshots handle |> List.map _.SnapshotId |> List.sort)

/// Writes the file again with new content and runs the backup: one more snapshot, with the old content now history.
let private revise (setup: Setup) (name: string) (text: string) (profile: Profile) : unit =
    // A moment apart, so that the file's modification time differs from the one recorded.
    Threading.Thread.Sleep 30
    File.WriteAllText(Path.Combine(setup.SourceDir, name), text)
    runOk quiet profile |> ignore

/// A target with three versions of a.txt, in three snapshots, and the profile that wrote them.
let private threeVersions (setup: Setup) : Profile =
    write setup "a.txt" "first content" |> ignore
    let profile = profileOf setup "p" None
    runOk quiet profile |> ignore
    revise setup "a.txt" "second content, longer" profile
    revise setup "a.txt" "third content, longer still" profile
    profile

let private overwrite (path: string) (text: string) : unit =
    File.SetAttributes(path, FileAttributes.Normal)
    File.WriteAllText(path, text)

let private pruneEnv (setup: Setup) (shouldStop: unit -> bool) (events: ResizeArray<ProgressEvent>) : PruneEnv =
    PruneRun.live (store setup) shouldStop events.Add

let private checkEnv (setup: Setup) (shouldStop: unit -> bool) (events: ResizeArray<ProgressEvent>) : CheckEnv =
    CheckRun.live (store setup) shouldStop events.Add

let private checkOf (setup: Setup) (quick: bool) : CheckResult =
    reader setup (fun handle -> CheckRun.execute (checkEnv setup (fun () -> false) (ResizeArray())) (store setup) handle quick)

module ``Manifest queries and the prune writer`` =
    [<Fact>]
    let ``Pruning with current files present`` () =
        within (fun setup ->
            let profile = threeVersions setup
            let ids = snapshotIds setup |> List.sort
            let removed = writer setup (fun handle -> ManifestMaintenance.removeSnapshots handle ids)
            // The two older snapshots hold no current row and go; the newest holds a.txt's current row and stays.
            Assert.Equal(2, removed)
            Assert.Equal<int64 list>([ ids[2] ], snapshotIds setup)
            let rows = query setup "SELECT snapshot_id, content_hash FROM file_versions"
            Assert.Equal(1, rows.Length)
            Assert.Equal(string (hashOf setup "a.txt" |> ContentHash.value), rows.Head.Split('|')[1])
            ignore profile)

    [<Fact>]
    let ``a snapshot that was not listed is untouched and an unlisted id is ignored`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let ids = snapshotIds setup |> List.sort
            let before = query setup "SELECT id, snapshot_id FROM file_versions ORDER BY id"
            // Only the oldest is listed, with an id that never existed.
            let removed = writer setup (fun handle -> ManifestMaintenance.removeSnapshots handle [ ids[0]; 9_999L ])
            Assert.Equal(1, removed)
            Assert.Equal<int64 list>([ ids[1]; ids[2] ], snapshotIds setup)
            // The rows of the unlisted snapshots are all still there.
            let after = query setup "SELECT id, snapshot_id FROM file_versions ORDER BY id"
            Assert.Equal<string list>(before |> List.filter (fun row -> not (row.EndsWith $"|{ids[0]}")), after))

    [<Fact>]
    let ``a deleted path's older rows are removed with their snapshot`` () =
        within (fun setup ->
            let path = write setup "gone.txt" "doomed content" |> ignore
            write setup "kept.txt" "kept content" |> ignore
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            File.Delete(Path.Combine(setup.SourceDir, "gone.txt"))
            runOk quiet profile |> ignore
            let ids = snapshotIds setup |> List.sort
            // Snapshot 1 holds both Added rows; gone.txt's latest row is the deletion in snapshot 2, which is not
            // current state, so expiring snapshot 1 removes its rows for gone.txt but protects kept.txt's.
            writer setup (fun handle -> ManifestMaintenance.removeSnapshots handle [ ids[0] ]) |> ignore
            let rows = query setup "SELECT relative_path, change_kind FROM file_versions ORDER BY id"
            Assert.Contains(rows, fun row -> row.EndsWith "|Deleted")
            Assert.Contains(rows, fun row -> row.Contains "kept.txt")
            Assert.DoesNotContain(rows, fun row -> row.Contains "gone.txt" && row.EndsWith "|Added")
            path)

    [<Fact>]
    let ``referenced hashes and sizes are read, with the paths that use them`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            write setup "b.txt" "alpha" |> ignore // the same content
            write setup "c.txt" "gamma gamma" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore

            reader setup (fun handle ->
                let sizes = ManifestMaintenance.referencedSizes handle
                Assert.Equal(2, sizes.Length)
                Assert.Equal<Set<ContentHash>>(sizes |> List.map fst |> Set.ofList, ManifestMaintenance.referencedHashes handle)
                Assert.Contains((hashOf setup "a.txt", 5L), sizes)
                Assert.Equal(2, (ManifestMaintenance.pathsForHash handle (hashOf setup "a.txt")).Length)
                Assert.Equal(1, (ManifestMaintenance.pathsForHash handle (hashOf setup "c.txt")).Length)))

    [<Fact>]
    let ``a link row has no hash, so it is never a referenced blob`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            let outside = Path.Combine(setup.Root, "outside")
            TempDir.write (Path.Combine(outside, "inside.txt")) "never copied"
            InteropTests.createJunction (Path.Combine(setup.SourceDir, "link")) outside
            runOk quiet (profileOf setup "p" None) |> ignore
            Assert.Equal(1, (query setup "SELECT id FROM file_versions WHERE change_kind = 'Linked'").Length)
            reader setup (fun handle -> Assert.Equal(1, (ManifestMaintenance.referencedSizes handle).Length)))

    [<Fact>]
    let ``the read-only queries still do not write`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            use connection = new Microsoft.Data.Sqlite.SqliteConnection $"Data Source={databaseOf setup};Pooling=False"
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- "PRAGMA wal_checkpoint(TRUNCATE);"
            command.ExecuteNonQuery() |> ignore
            connection.Close()
            let database = databaseOf setup
            let fingerprint () = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes database)), File.GetLastWriteTimeUtc database
            let before = fingerprint ()

            reader setup (fun handle ->
                ManifestMaintenance.referencedHashes handle |> ignore
                ManifestMaintenance.referencedSizes handle |> ignore
                ManifestMaintenance.pathsForHash handle (hashOf setup "a.txt") |> ignore)

            Assert.Equal(before, fingerprint ()))

    [<Fact>]
    let ``a target without a manifest has nothing to prune and none is created`` () =
        within (fun setup ->
            Directory.CreateDirectory setup.TargetDir |> ignore
            let result =
                writer setup (fun handle ->
                    Assert.Empty(ManifestMaintenance.referencedHashes handle)
                    PruneRun.execute (pruneEnv setup (fun () -> false) (ResizeArray())) (store setup) handle keepNothing ValueNone)

            Assert.Equal(0, result.SnapshotsRemoved)
            Assert.False(File.Exists(databaseOf setup)))

module ``Content store listing and deletion`` =
    [<Fact>]
    let ``stored hashes are the blob names and a read-only blob can be deleted`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore
            let hash = hashOf setup "a.txt"
            Assert.Equal<Set<ContentHash>>(set [ hash ], ContentStore.storedHashes (store setup))
            // The blob is a hard link target of the mirror entry, so it is read-only.
            Assert.True(File.GetAttributes(blobOf setup hash).HasFlag FileAttributes.ReadOnly)
            ContentStore.deleteBlob (store setup) hash
            Assert.False(File.Exists(blobOf setup hash))
            // Deleting what is not there is not an error.
            ContentStore.deleteBlob (store setup) hash)

    [<Fact>]
    let ``a store that does not exist has no blobs`` () =
        within (fun setup -> Assert.Empty(ContentStore.storedHashes (store setup)))

module ``Live mirror unaffected by pruning`` =
    [<Fact>]
    let ``Pruning with current files present`` () =
        within (fun setup ->
            let profile = threeVersions setup
            let mirror = mirrorFile setup "a.txt"
            let content = File.ReadAllText mirror
            let events = ResizeArray()
            let result = writer setup (fun handle -> PruneRun.execute (pruneEnv setup (fun () -> false) events) (store setup) handle keepNothing ValueNone)
            Assert.Equal(2, result.SnapshotsRemoved)
            // The live mirror is intact, and so is the content that backs it.
            Assert.Equal(content, File.ReadAllText mirror)
            Assert.True(File.Exists(blobOf setup (hashOf setup "a.txt")))
            ignore profile)

module ``Unreferenced content garbage collection`` =
    [<Fact>]
    let ``Content only referenced by pruned snapshots`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let first = firstHashOf setup "a.txt"
            let latest = hashOf setup "a.txt"
            // Older content is protected read-only after the mirror moved on: the prune still removes it.
            Assert.True(File.GetAttributes(blobOf setup first).HasFlag FileAttributes.ReadOnly)
            let events = ResizeArray()
            let result = writer setup (fun handle -> PruneRun.execute (pruneEnv setup (fun () -> false) events) (store setup) handle keepNothing ValueNone)
            Assert.Equal(2, result.BlobsRemoved)
            Assert.Empty result.Failed
            Assert.False result.Stopped
            Assert.False(File.Exists(blobOf setup first))
            Assert.True(File.Exists(blobOf setup latest))
            // The progress told the total once, then one item per blob.
            Assert.Equal(ProgressEvent.TransferStarting(0L, 2), events[0])
            Assert.Equal(2, events |> Seq.filter (fun event -> event = ProgressEvent.FileTransferred) |> Seq.length))

    /// A target whose manifest holds the given rows, one list per snapshot (ids 1, 2, ...), each hash backed by a blob.
    let private seeded (setup: Setup) (snapshots: (string * string * ChangeKind) list list) : unit =
        let at (minutes: int) = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(float minutes)
        Directory.CreateDirectory(Path.GetDirectoryName(databaseOf setup) |> nonNull) |> ignore
        ContentStore.ensureDirectories (store setup)
        let handle = SqliteManifest.openAt (databaseOf setup) true

        try
            snapshots
            |> List.iteri (fun index rows ->
                let snapshot = SqliteManifest.beginSnapshot handle (at index)
                let mutable batch = SqliteManifest.beginBatch handle

                for path, hashText, kind in rows do
                    let blob = blobOf setup (hash hashText)
                    Directory.CreateDirectory(Path.GetDirectoryName blob |> nonNull) |> ignore
                    File.WriteAllText(blob, hashText)

                    SqliteManifest.record
                        batch
                        snapshot
                        { RowPath = MirrorPath.ofString path
                          PreviousPath = ValueNone
                          Content =
                            RowContent.Regular
                                { Hash = hash hashText
                                  Size = 10L
                                  SourceModifiedAt = at 0
                                  Quick = ValueNone }
                          Kind = kind
                          RecordedAt = at index }

                SqliteManifest.finishSnapshot batch snapshot SnapshotStatus.Complete (at index) (Run.initial (at index)).Counts
                batch <- SqliteManifest.checkpoint batch
                SqliteManifest.discard batch)
        finally
            SqliteManifest.close handle

    [<Fact>]
    let ``Path deleted under another casing no longer keeps its snapshot or content`` () =
        within (fun setup ->
            seeded
                setup
                [ [ ("Äiti.txt", "aa11", ChangeKind.Added) ]
                  [ ("äiti.txt", "bb22", ChangeKind.Changed) ]
                  [ ("äiti.txt", "bb22", ChangeKind.Deleted) ] ]

            let result = writer setup (fun handle -> PruneRun.execute (pruneEnv setup (fun () -> false) (ResizeArray())) (store setup) handle keepNothing ValueNone)
            Assert.Equal<int64 list>([ 3L ], snapshotIds setup)
            // The content only the expired records referenced goes; the deletion row still names bb22.
            Assert.False(File.Exists(blobOf setup (hash "aa11")))
            Assert.True(File.Exists(blobOf setup (hash "bb22")))
            Assert.Equal(1, result.BlobsRemoved))

    [<Fact>]
    let ``Current record of a path recorded under several casings is kept`` () =
        for older, newer in [ "Äiti.txt", "äiti.txt"; "äiti.txt", "Äiti.txt" ] do
            within (fun setup ->
                seeded setup [ [ (older, "aa11", ChangeKind.Added) ]; [ (newer, "bb22", ChangeKind.Changed) ] ]
                let removed = writer setup (fun handle -> ManifestMaintenance.removeSnapshots handle [ 1L ])
                Assert.Equal(1, removed)
                Assert.Equal<int64 list>([ 2L ], snapshotIds setup)
                let rows = query setup "SELECT snapshot_id, content_hash FROM file_versions"
                Assert.Equal<string list>([ "2|bb22" ], rows)
                Assert.True(File.Exists(blobOf setup (hash "bb22")))
                let state = reader setup SqliteManifest.currentState
                Assert.Equal(1, state.Count)
                Assert.True(state.ContainsKey older))

    [<Fact>]
    let ``No unreferenced content to remove`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore
            let events = ResizeArray()
            let result = writer setup (fun handle -> PruneRun.execute (pruneEnv setup (fun () -> false) events) (store setup) handle keepNothing ValueNone)
            Assert.Equal(0, result.BlobsRemoved)
            // No total was published, so no bar for zero items is drawn.
            Assert.Empty events)

    [<Fact>]
    let ``a blob that cannot be deleted because of an I/O error is recorded and the run goes on`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let attempts = ResizeArray<ContentHash>()

            let env =
                { pruneEnv setup (fun () -> false) (ResizeArray()) with
                    DeleteBlob =
                        fun hash ->
                            attempts.Add hash

                            if attempts.Count = 1 then
                                raise (IOException "the file is in use")
                            else
                                ContentStore.deleteBlob (store setup) hash }

            let result = writer setup (fun handle -> PruneRun.execute env (store setup) handle keepNothing ValueNone)
            Assert.Equal(1, result.BlobsRemoved)
            let failure = List.exactlyOne result.Failed
            Assert.Equal(attempts[0], failure.Blob)
            Assert.Contains("in use", failure.Reason)
            Assert.Equal(Outcome.PartialFailure, PruneView.outcome result.Failed result.Stopped))

    [<Fact>]
    let ``any other exception is not swallowed and keeps its stack`` () =
        within (fun setup ->
            threeVersions setup |> ignore

            let env =
                { pruneEnv setup (fun () -> false) (ResizeArray()) with
                    DeleteBlob = fun _ -> raise (InvalidOperationException "a bug") }

            let error =
                Assert.Throws<InvalidOperationException>(fun () ->
                    writer setup (fun handle -> PruneRun.execute env (store setup) handle keepNothing ValueNone) |> ignore)

            Assert.Equal("a bug", error.Message)
            Assert.False(String.IsNullOrEmpty error.StackTrace))

    [<Fact>]
    let ``only the confirmed snapshots are removed`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let ids = snapshotIds setup |> List.sort
            let result = writer setup (fun handle -> PruneRun.execute (pruneEnv setup (fun () -> false) (ResizeArray())) (store setup) handle keepNothing (ValueSome [ ids[0] ]))
            Assert.Equal(1, result.SnapshotsRemoved)
            Assert.Equal<int64 list>([ ids[1]; ids[2] ], snapshotIds setup))

    [<Fact>]
    let ``the target's run lock is taken and a busy target is a refusal`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let target = absolute setup.TargetDir
            use _held = RunLock.tryAcquire "other" target |> Result.defaultWith (fun _ -> failwith "no lock")

            match PruneRun.run (pruneEnv setup (fun () -> false) (ResizeArray())) "p" target keepNothing ValueNone with
            | Error(BackupRefusal.TargetBusy _ as refusal) ->
                Assert.Contains("a prune run for profile 'p'", BackupRefusal.renderFor "prune" refusal)
            | other -> failwith $"expected a refusal, found {other}"

            // Nothing was removed.
            Assert.Equal(3, (snapshotIds setup).Length))

/// Simulates an abrupt loss of power: only the main database file survives, so whatever sat in the write-ahead log is
/// gone. Copies the file (not its `-wal`) and returns the copy's rows as `snapshot ids` and `content hashes`.
let private survivingManifest (setup: Setup) : int64 list * string list =
    let copy = Path.Combine(setup.Root, "after-power-loss.db")
    File.Copy(databaseOf setup, copy, true)

    use connection = new Microsoft.Data.Sqlite.SqliteConnection $"Data Source={copy};Pooling=False;Mode=ReadOnly"
    connection.Open()

    let column (sql: string) (read: Microsoft.Data.Sqlite.SqliteDataReader -> 'a) : 'a list =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        use reader = command.ExecuteReader()
        [ while reader.Read() do yield read reader ]

    column "SELECT id FROM snapshots ORDER BY id" (fun r -> r.GetInt64 0),
    column "SELECT DISTINCT content_hash FROM file_versions WHERE content_hash IS NOT NULL" (fun r -> r.GetString 0)

module ``Snapshot removal is durable before content is removed`` =
    [<Fact>]
    let ``Snapshot removal is on persistent storage before the first content removal`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let ids = snapshotIds setup |> List.sort
            let seen = ResizeArray<int64 list>()

            let env =
                { pruneEnv setup (fun () -> false) (ResizeArray()) with
                    DeleteBlob =
                        fun hash ->
                            if seen.Count = 0 then
                                seen.Add(fst (survivingManifest setup))

                            ContentStore.deleteBlob (store setup) hash }

            writer setup (fun handle -> PruneRun.execute env (store setup) handle keepNothing ValueNone) |> ignore
            Assert.Equal(1, seen.Count)
            // Only the newest snapshot, which holds the current row, is left in what survived.
            Assert.Equal<int64 list>([ ids[2] ], seen[0]))

    [<Fact>]
    let ``No remaining record references removed content after an abrupt loss`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let deleted = ResizeArray<string>()
            let dangling = ResizeArray<string>()

            let env =
                { pruneEnv setup (fun () -> false) (ResizeArray()) with
                    DeleteBlob =
                        fun hash ->
                            ContentStore.deleteBlob (store setup) hash
                            deleted.Add(ContentHash.value hash)
                            let _, referenced = survivingManifest setup
                            dangling.AddRange(referenced |> List.filter deleted.Contains) }

            let result = writer setup (fun handle -> PruneRun.execute env (store setup) handle keepNothing ValueNone)
            Assert.Equal(2, result.BlobsRemoved)
            Assert.Empty dangling)

module ``Graceful cancellation via CtrlC`` =
    [<Fact>]
    let ``Single Ctrl+C before retention evaluation and snapshot removal have started`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let blobs = ContentStore.storedHashes (store setup)
            let result = writer setup (fun handle -> PruneRun.execute (pruneEnv setup (fun () -> true) (ResizeArray())) (store setup) handle keepNothing ValueNone)
            Assert.True result.Stopped
            Assert.Equal(0, result.SnapshotsRemoved)
            Assert.Equal(0, result.BlobsRemoved)
            Assert.Equal(3, (snapshotIds setup).Length)
            Assert.Equal<Set<ContentHash>>(blobs, ContentStore.storedHashes (store setup)))

    [<Fact>]
    let ``Single Ctrl+C during garbage collection`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let deleted = ref 0

            let env =
                { pruneEnv setup (fun () -> deleted.Value >= 1) (ResizeArray()) with
                    DeleteBlob =
                        fun hash ->
                            ContentStore.deleteBlob (store setup) hash
                            deleted.Value <- deleted.Value + 1 }

            let result = writer setup (fun handle -> PruneRun.execute env (store setup) handle keepNothing ValueNone)
            // The snapshot removal had completed and stays; the blob in progress finished; the next was not started.
            Assert.True result.Stopped
            Assert.Equal(2, result.SnapshotsRemoved)
            Assert.Equal(1, result.BlobsRemoved)
            Assert.Equal(2, (ContentStore.storedHashes (store setup)).Count))

    [<Fact>]
    let ``Single Ctrl+C during verification`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let seen = ref 0

            let events = ResizeArray()

            let env =
                { checkEnv setup (fun () -> seen.Value >= 1) events with
                    Publish =
                        fun event ->
                            events.Add event

                            if event = ProgressEvent.FileTransferred then
                                seen.Value <- seen.Value + 1 }

            let result = reader setup (fun handle -> CheckRun.execute env (store setup) handle false)
            // The blob in progress finished and was counted; no further blob was started.
            Assert.True result.Stopped
            Assert.Equal(1, result.Checked))

module ``Check command verifies stored content against the manifest`` =
    [<Fact>]
    let ``All referenced content intact`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let result = checkOf setup false
            Assert.Equal(3, result.Checked)
            Assert.False(Check.hasProblems result)
            Assert.Equal(Outcome.Success, Check.outcome result))

    [<Fact>]
    let ``Referenced blob missing from the store`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let first = firstHashOf setup "a.txt"
            File.SetAttributes(blobOf setup first, FileAttributes.Normal)
            File.Delete(blobOf setup first)
            let result = checkOf setup false
            let finding = List.exactlyOne result.Missing
            Assert.Equal(first, finding.Hash)
            Assert.Contains(mirrorKey setup "a.txt", finding.Paths)
            Assert.Empty result.Corrupt
            Assert.Equal(Outcome.PartialFailure, Check.outcome result))

    [<Fact>]
    let ``Referenced blob present but corrupted`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let first = firstHashOf setup "a.txt"
            overwrite (blobOf setup first) "tampered"
            let result = checkOf setup false
            let finding = List.exactlyOne result.Corrupt
            Assert.Equal(first, finding.Hash)
            Assert.Contains(mirrorKey setup "a.txt", finding.Paths)
            Assert.Empty result.Missing)

    [<Fact>]
    let ``Unreferenced content present in the store`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let extra = hash "0123456789abcdef0123456789abcdef"
            TempDir.write (blobOf setup extra) "an orphan"
            let result = checkOf setup false
            Assert.Equal<ContentHash list>([ extra ], result.Orphaned)
            // Informational only: not a problem, and not deleted.
            Assert.Equal(Outcome.Success, Check.outcome result)
            Assert.True(File.Exists(blobOf setup extra)))

    [<Fact>]
    let ``Profile contains a tracked symlink or junction`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            let outside = Path.Combine(setup.Root, "outside")
            TempDir.write (Path.Combine(outside, "inside.txt")) "never copied"
            InteropTests.createJunction (Path.Combine(setup.SourceDir, "link")) outside
            runOk quiet (profileOf setup "p" None) |> ignore
            let result = checkOf setup false
            Assert.Equal(1, result.Checked)
            Assert.Empty result.Missing
            Assert.Empty result.Corrupt)

    [<Fact>]
    let ``a check does not write the target or the manifest`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let listing () =
                Directory.EnumerateFiles(setup.TargetDir, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun path -> not (path.Contains "profile.db"))
                |> Seq.map (fun path -> path, File.GetLastWriteTimeUtc path)
                |> List.ofSeq

            let before = listing ()
            checkOf setup false |> ignore
            checkOf setup true |> ignore
            Assert.Equal<(string * DateTime) list>(before, listing ()))

module ``Quick mode trades thoroughness for speed`` =
    [<Fact>]
    let ``Quick check skips content re-hashing`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            let first = firstHashOf setup "a.txt"
            let second = hashOf setup "a.txt"
            overwrite (blobOf setup first) "tampered"
            let events = ResizeArray()

            let result =
                reader setup (fun handle -> CheckRun.execute (checkEnv setup (fun () -> false) events) (store setup) handle true)

            // The changed blob is present, so it is not reported; and no byte was read.
            Assert.Empty result.Corrupt
            Assert.Empty result.Missing
            Assert.DoesNotContain(events, fun event -> match event with ProgressEvent.BytesCopied _ -> true | _ -> false)

            // A missing blob is reported exactly as in the default mode.
            File.SetAttributes(blobOf setup second, FileAttributes.Normal)
            File.Delete(blobOf setup second)
            Assert.Equal(1, (checkOf setup true).Missing.Length))

module ``Check reports workload progress and estimated time remaining`` =
    [<Fact>]
    let ``Missing blob advances full-check progress`` () =
        within (fun setup ->
            write setup "a.txt" "alpha content" |> ignore
            write setup "b.txt" "bravo, a longer content" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore
            let gone = hashOf setup "b.txt"
            File.SetAttributes(blobOf setup gone, FileAttributes.Normal)
            File.Delete(blobOf setup gone)
            let events = ResizeArray()

            let result =
                reader setup (fun handle -> CheckRun.execute (checkEnv setup (fun () -> false) events) (store setup) handle false)

            Assert.Equal(1, result.Missing.Length)
            // Both blobs are counted as checked; the missing one's size is credited and none of its bytes were read.
            Assert.Equal(2, events |> Seq.filter (fun event -> event = ProgressEvent.FileTransferred) |> Seq.length)
            Assert.Contains(ProgressEvent.WorkCredited(int64 "bravo, a longer content".Length), events)
            let read = events |> Seq.sumBy (fun event -> match event with ProgressEvent.BytesCopied delta -> delta | _ -> 0L)
            Assert.Equal(int64 "alpha content".Length, read)
            Assert.Equal(ProgressEvent.TransferStarting(int64 ("alpha content".Length + "bravo, a longer content".Length), 2), events[0]))

    [<Fact>]
    let ``a blob shorter than recorded still completes its share of the work`` () =
        within (fun setup ->
            threeVersions setup |> ignore
            overwrite (blobOf setup (firstHashOf setup "a.txt")) "short"
            let events = ResizeArray()
            let result = reader setup (fun handle -> CheckRun.execute (checkEnv setup (fun () -> false) events) (store setup) handle false)
            Assert.Equal(1, result.Corrupt.Length)
            // The recorded size is credited beyond the bytes that were there to read, so the run ends at 100%.
            let tracker = events |> Seq.fold (fun tracker event -> Tracker.apply DateTimeOffset.UtcNow event tracker) Tracker.initial
            let tracker = Tracker.refresh TimeSpan.Zero DateTimeOffset.UtcNow (Tracker.apply DateTimeOffset.UtcNow (ProgressEvent.RunEnded Outcome.PartialFailure) tracker)
            Assert.Equal(100.0, Tracker.percentBy Basis.Bytes tracker, 6))

    [<Fact>]
    let ``Empty or zero-size workload completes cleanly`` () =
        within (fun setup ->
            // A target with nothing recorded: no blobs, a total of zero, and no failure.
            Directory.CreateDirectory setup.TargetDir |> ignore
            let events = ResizeArray()
            let result = reader setup (fun handle -> CheckRun.execute (checkEnv setup (fun () -> false) events) (store setup) handle false)
            Assert.Equal(0, result.Checked)
            Assert.Equal(ProgressEvent.TransferStarting(0L, 0), events[0])
            let tracker = events |> Seq.fold (fun tracker event -> Tracker.apply DateTimeOffset.UtcNow event tracker) Tracker.initial
            Assert.Equal(100.0, Tracker.percentBy Basis.Bytes tracker, 6))
