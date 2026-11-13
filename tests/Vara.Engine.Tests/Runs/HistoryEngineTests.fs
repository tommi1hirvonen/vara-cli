/// Tests of the history effects against a real target written by the backup: the read-only manifest queries, the
/// restore execution and the live-mirror guard. Spec-named modules hold the scenarios that belong to the effects.
module Vara.Engine.Tests.HistoryEngineTests

open System
open System.IO
open System.Security.Cryptography
open Xunit
open Vara.Domain
open Vara.Engine
open Vara.Engine.Tests
open Vara.Engine.Tests.BackupRunTests

/// A blob that fails with an I/O error once it has delivered `after` bytes: a write interrupted part-way.
type FailingStream(inner: Stream, after: int) =
    inherit Stream()
    let mutable delivered = 0

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Read(buffer: Span<byte>) : int =
        if delivered >= after then
            raise (IOException "The disk failed while the content was being copied.")

        let read = inner.Read(buffer.Slice(0, min buffer.Length (after - delivered)))
        delivered <- delivered + read
        read

    override this.Read(buffer: byte[], offset: int, count: int) : int = this.Read(Span<byte>(buffer, offset, count))
    override _.Flush() = ()
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_: int64) = raise (NotSupportedException())
    override _.Write(_: byte[], _: int, _: int) = raise (NotSupportedException())

    override _.Dispose(disposing: bool) =
        if disposing then inner.Dispose()
        base.Dispose disposing

/// A blob that raises something that is not an I/O error: a bug, which must not be swallowed as a per-file failure.
type ExplodingStream(inner: Stream) =
    inherit Stream()

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Read(_: Span<byte>) : int = raise (InvalidOperationException "boom from ExplodingStream")
    override this.Read(buffer: byte[], offset: int, count: int) : int = this.Read(Span<byte>(buffer, offset, count))
    override _.Flush() = ()
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_: int64) = raise (NotSupportedException())
    override _.Write(_: byte[], _: int, _: int) = raise (NotSupportedException())

    override _.Dispose(disposing: bool) =
        if disposing then inner.Dispose()
        base.Dispose disposing

/// Folds the write-ahead log into the database file and closes for good, so that the file is as it will stay. The
/// connection is not pooled, so nothing closes (and changes the file) later.
let private settle (database: string) : unit =
    use connection = new Microsoft.Data.Sqlite.SqliteConnection $"Data Source={database};Pooling=False"
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "PRAGMA wal_checkpoint(TRUNCATE);"
    command.ExecuteNonQuery() |> ignore

let private databaseOf (setup: Setup) : string = Path.Combine(setup.TargetDir, ".vara", "profile.db")

let private store (setup: Setup) : StoreLayout = ContentStore.layout setup.TargetDir

let private withManifest (setup: Setup) (body: ManifestHandle -> 'a) : 'a =
    let handle = SqliteManifest.openReadOnly (databaseOf setup)

    try
        body handle
    finally
        SqliteManifest.close handle

/// The version of a source file that the manifest holds latest, as the history sees it.
let private latestVersion (setup: Setup) (name: string) : FileVersion =
    withManifest setup (fun handle ->
        ManifestQueries.allVersions handle
        |> History.stateAt Moment.Latest
        |> Array.find (fun version -> MirrorPath.value version.Row.RowPath = mirrorKey setup name))

let private realEnv (setup: Setup) (shouldStop: unit -> bool) (events: ResizeArray<ProgressEvent>) : RestoreEnv =
    { OpenBlob = ContentStore.openRead (store setup)
      ShouldStop = shouldStop
      Publish = fun event -> lock events (fun () -> events.Add event) }

let private planFor (setup: Setup) (name: string) (destination: string) : RestorePlan =
    match Restore.planFile name (Destination.Explicit destination) (latestVersion setup name) with
    | Ok plan -> plan
    | Error error -> failwith (HistoryError.render error)

let private hashOf (path: string) : string = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path))

module ``Manifest queries`` =
    [<Fact>]
    let ``Rows written by the backup are read back`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            write setup "sub\\b.txt" "beta" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore

            withManifest setup (fun handle ->
                let versions = ManifestQueries.allVersions handle |> List.ofSeq
                Assert.Equal(2, versions.Length)
                Assert.All(versions, fun version -> Assert.Equal(ChangeKind.Added, version.Row.Kind))
                let state = History.stateAt Moment.Latest versions
                Assert.Equal(2, state.Length)

                match ManifestQueries.snapshots handle with
                | [ only ] ->
                    Assert.Equal(SnapshotStatus.Complete, only.Status)
                    Assert.Equal(2, only.Stats.FilesAdded)
                    Assert.Equal(only.SnapshotId, versions.Head.InSnapshot)
                | other -> failwith $"expected one snapshot, found {other.Length}"))

    [<Fact>]
    let ``Snapshots are read newest first`` () =
        within (fun setup ->
            let path = write setup "a.txt" "one"
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            File.WriteAllText(path, "two, longer")
            runOk quiet profile |> ignore

            withManifest setup (fun handle ->
                let ids = ManifestQueries.snapshots handle |> List.map _.SnapshotId
                Assert.Equal<int64 list>(ids |> List.sortDescending, ids)
                Assert.Equal(2, ids.Length)))

    [<Fact>]
    let ``A moved file's history is followed through the manifest`` () =
        within (fun setup ->
            let path = write setup "old.txt" "moving content that is long enough"
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            File.Move(path, Path.Combine(setup.SourceDir, "new.txt"))
            runOk quiet profile |> ignore

            withManifest setup (fun handle ->
                let history =
                    History.versionsOf (ManifestQueries.versionsOfPath handle) (MirrorPath.ofString (mirrorKey setup "new.txt"))

                Assert.Equal<ChangeKind list>(
                    [ ChangeKind.Moved; ChangeKind.Deleted; ChangeKind.Added ],
                    history |> List.map _.Row.Kind
                )))

    [<Fact>]
    let ``Path and prefix lookups ignore letter case`` () =
        within (fun setup ->
            write setup "Docs\\A.txt" "alpha" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore

            withManifest setup (fun handle ->
                let key = mirrorKey setup "Docs\\A.txt"
                Assert.True(ManifestQueries.hasPath handle (key.ToUpperInvariant()))
                Assert.False(ManifestQueries.hasPath handle (key + "x"))
                let docs = key.Substring(0, key.IndexOf("Docs", StringComparison.OrdinalIgnoreCase) + 4)
                Assert.True(ManifestQueries.hasPrefix handle ((Prefix.ofDirectory docs).ToUpperInvariant()))
                Assert.False(ManifestQueries.hasPrefix handle "nothing\\")))

    [<Fact>]
    let ``No query writes`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore
            let database = databaseOf setup
            settle database
            let before = hashOf database
            let stamp = File.GetLastWriteTimeUtc database

            withManifest setup (fun handle ->
                ManifestQueries.allVersions handle |> Seq.length |> ignore
                ManifestQueries.snapshots handle |> ignore
                ManifestQueries.versionsOfPath handle (MirrorPath.ofString (mirrorKey setup "a.txt")) |> ignore
                ManifestQueries.hasPath handle "x" |> ignore
                ManifestQueries.hasPrefix handle "x\\" |> ignore)

            settle database
            Assert.Equal(before, hashOf database)
            Assert.Equal(stamp, File.GetLastWriteTimeUtc database))

    [<Fact>]
    let ``A target without a manifest is read as empty and nothing is created`` () =
        within (fun setup ->
            let before = Directory.Exists setup.TargetDir

            withManifest setup (fun handle ->
                Assert.Empty(ManifestQueries.allVersions handle)
                Assert.Empty(ManifestQueries.snapshots handle)
                Assert.False(ManifestQueries.hasPrefix handle "x\\"))

            Assert.Equal(before, Directory.Exists setup.TargetDir)
            Assert.False(Directory.Exists(Path.Combine(setup.TargetDir, ".vara"))))

module ``Restore writes are atomic`` =
    [<Fact>]
    let ``Restore completes successfully over an existing destination file`` () =
        within (fun setup ->
            let source = write setup "a.txt" "restored content"
            File.SetLastWriteTimeUtc(source, DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc))
            runOk quiet (profileOf setup "p" None) |> ignore
            let destination = Path.Combine(setup.Root, "out", "a.txt")
            TempDir.write destination "old"
            File.SetAttributes(destination, FileAttributes.ReadOnly)
            let events = ResizeArray()

            let result = RestoreRun.execute (realEnv setup (fun () -> false) events) (planFor setup "a.txt" destination)

            Assert.Equal(1, result.Written)
            Assert.Empty result.Failed
            Assert.Equal("restored content", File.ReadAllText destination)
            // No staging file stays beside the destination.
            Assert.Equal<string list>([ destination ], Directory.GetFiles(Path.GetDirectoryName destination |> nonNull) |> List.ofArray)
            Assert.Equal(DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc), File.GetLastWriteTimeUtc destination)
            Assert.Contains(ProgressEvent.FileTransferred, events)

            let copied =
                events |> Seq.sumBy (function ProgressEvent.BytesCopied delta -> delta | _ -> 0L)

            Assert.Equal(int64 "restored content".Length, copied))

    [<Fact>]
    let ``Restore is interrupted while writing over an existing destination file`` () =
        within (fun setup ->
            write setup "a.txt" "restored content that is long enough" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore
            let destination = Path.Combine(setup.Root, "out", "a.txt")
            TempDir.write destination "original"
            let events = ResizeArray()

            // The blob fails after its first bytes: the write is interrupted part-way.
            let failing =
                { realEnv setup (fun () -> false) events with
                    OpenBlob =
                        fun hash ->
                            let real = ContentStore.openRead (store setup) hash
                            new FailingStream(real, 4) :> Stream }

            let result = RestoreRun.execute failing (planFor setup "a.txt" destination)

            Assert.Equal(0, result.Written)
            Assert.Equal<string list>([ destination ], result.Failed |> List.map _.Destination)
            Assert.Contains("The disk failed", result.Failed.Head.Reason)
            Assert.Equal("original", File.ReadAllText destination)
            Assert.Equal<string list>([ destination ], Directory.GetFiles(Path.GetDirectoryName destination |> nonNull) |> List.ofArray))

    [<Fact>]
    let ``an unexpected exception is rethrown with its original stack after the cleanup`` () =
        within (fun setup ->
            write setup "a.txt" "restored content that is long enough" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore
            let destination = Path.Combine(setup.Root, "out", "a.txt")
            TempDir.write destination "original"
            let events = ResizeArray()

            let exploding =
                { realEnv setup (fun () -> false) events with
                    OpenBlob =
                        fun hash ->
                            let real = ContentStore.openRead (store setup) hash
                            new ExplodingStream(real) :> Stream }

            let error =
                Assert.Throws<InvalidOperationException>(fun () ->
                    RestoreRun.execute exploding (planFor setup "a.txt" destination) |> ignore)

            // The stack still names the place the exception was raised, and the staging file is gone.
            Assert.Contains("ExplodingStream", error.StackTrace |> nonNull)
            Assert.Equal("original", File.ReadAllText destination)
            Assert.Equal<string list>([ destination ], Directory.GetFiles(Path.GetDirectoryName destination |> nonNull) |> List.ofArray)
            // The file that was in progress is closed in the display even though the restore threw.
            Assert.Contains(ProgressEvent.FileFinished destination, events))

module ``Restored content is verified against its recorded hash`` =
    /// The blob's bytes with the first one changed: damaged content of the same length.
    let private damaged (real: Stream) : Stream =
        use real = real
        use buffer = new MemoryStream()
        real.CopyTo buffer
        let bytes = buffer.ToArray()
        bytes[0] <- bytes[0] ^^^ 0xFFuy
        new MemoryStream(bytes) :> Stream

    let private readAll (stream: Stream) : byte[] =
        use stream = stream
        use buffer = new MemoryStream()
        stream.CopyTo buffer
        buffer.ToArray()

    [<Fact>]
    let ``Stored content does not match its recorded hash`` () =
        within (fun setup ->
            write setup "a.txt" "restored content that is long enough" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore
            let destination = Path.Combine(setup.Root, "out", "a.txt")
            TempDir.write destination "original"
            let events = ResizeArray()

            let env =
                { realEnv setup (fun () -> false) events with
                    OpenBlob = fun hash -> damaged (ContentStore.openRead (store setup) hash) }

            let result = RestoreRun.execute env (planFor setup "a.txt" destination)

            Assert.Equal(0, result.Written)
            Assert.Equal<string list>([ destination ], result.Failed |> List.map _.Destination)
            Assert.Contains("does not match", result.Failed.Head.Reason)
            Assert.Equal("original", File.ReadAllText destination)
            Assert.Equal<string list>([ destination ], Directory.GetFiles(Path.GetDirectoryName destination |> nonNull) |> List.ofArray))

    [<Fact>]
    let ``One damaged file in a directory restore`` () =
        within (fun setup ->
            for name in [ "a.txt"; "b.txt"; "c.txt" ] do
                write setup $"d\\{name}" $"content of {name}" |> ignore

            runOk quiet (profileOf setup "p" None) |> ignore
            let directory = Path.GetDirectoryName(mirrorKey setup "d\\a.txt") |> nonNull
            let outDir = Path.Combine(setup.Root, "out")

            let plan =
                withManifest setup (fun handle ->
                    match Restore.planDirectory directory Moment.Latest (Destination.Explicit outDir) (ManifestQueries.allVersions handle) with
                    | Ok plan -> plan
                    | Error error -> failwith (HistoryError.render error))

            let events = ResizeArray()
            let victim = Text.Encoding.UTF8.GetBytes "content of b.txt"

            let env =
                { realEnv setup (fun () -> false) events with
                    OpenBlob =
                        fun hash ->
                            let real = ContentStore.openRead (store setup) hash
                            let bytes = readAll real

                            if bytes = victim then
                                bytes[0] <- bytes[0] ^^^ 0xFFuy

                            new MemoryStream(bytes) :> Stream }

            let result = RestoreRun.execute env plan

            Assert.False result.Stopped
            Assert.Equal(2, result.Written)
            Assert.Equal<string list>([ Path.Combine(outDir, "b.txt") ], result.Failed |> List.map _.Destination)
            Assert.Contains("does not match", result.Failed.Head.Reason)
            Assert.True(File.Exists(Path.Combine(outDir, "a.txt")))
            Assert.True(File.Exists(Path.Combine(outDir, "c.txt")))
            Assert.False(File.Exists(Path.Combine(outDir, "b.txt")))
            Assert.Equal(2, Directory.GetFiles outDir |> Array.length))

module ``Graceful cancellation of a directory restore via CtrlC`` =
    let private threeFiles (setup: Setup) : RestorePlan =
        for name in [ "a.txt"; "b.txt"; "c.txt" ] do
            write setup $"d\\{name}" $"content of {name}" |> ignore

        runOk quiet (profileOf setup "p" None) |> ignore

        let directory = Path.GetDirectoryName(mirrorKey setup "d\\a.txt") |> nonNull

        withManifest setup (fun handle ->
            match Restore.planDirectory directory Moment.Latest (Destination.Explicit(Path.Combine(setup.Root, "out"))) (ManifestQueries.allVersions handle) with
            | Ok plan -> plan
            | Error error -> failwith (HistoryError.render error))

    [<Fact>]
    let ``Single Ctrl+C during a directory restore's writes`` () =
        within (fun setup ->
            let plan = threeFiles setup
            let events = ResizeArray()
            // The stop is requested while the first file is being written; the file finishes, no other starts.
            let stop = ref false

            let env =
                { realEnv setup (fun () -> stop.Value) events with
                    Publish =
                        fun event ->
                            lock events (fun () -> events.Add event)

                            match event with
                            | ProgressEvent.FileStarted _ -> stop.Value <- true
                            | _ -> () }

            let result = RestoreRun.execute env plan
            Assert.True result.Stopped
            Assert.Equal(1, result.Written)
            Assert.Equal(1, events |> Seq.filter (function ProgressEvent.FileStarted _ -> true | _ -> false) |> Seq.length))

    [<Fact>]
    let ``Cancellation does not roll back already-applied paths`` () =
        within (fun setup ->
            let plan = threeFiles setup
            let events = ResizeArray()
            let stop = ref false

            let env =
                { realEnv setup (fun () -> stop.Value) events with
                    Publish =
                        fun event ->
                            match event with
                            | ProgressEvent.FileTransferred -> stop.Value <- true
                            | _ -> () }

            let result = RestoreRun.execute env plan
            Assert.True result.Stopped
            let first = plan.Writes.Head.Destination
            Assert.True(File.Exists first)
            Assert.Equal(1, Directory.GetFiles(Path.Combine(setup.Root, "out")).Length))

    [<Fact>]
    let ``removals are applied before the writes and counted apart`` () =
        within (fun setup ->
            let plan = threeFiles setup
            let stale = Path.Combine(setup.Root, "out", "stale.txt")
            TempDir.write stale "stale"
            let plan = { plan with Removals = [ stale ] }
            let result = RestoreRun.execute (realEnv setup (fun () -> false) (ResizeArray())) plan
            Assert.Equal(1, result.Removed)
            Assert.Equal(3, result.Written)
            Assert.False(File.Exists stale))

module ``Restore never writes into the live mirror`` =
    let private writePlan (setup: Setup) (destination: string) : RestorePlan =
        write setup "a.txt" "alpha" |> ignore
        runOk quiet (profileOf setup "p" None) |> ignore
        planFor setup "a.txt" destination

    let private refused (result: Result<unit, HistoryError>) : bool =
        match result with
        | Error(HistoryError.DestinationInMirror _) -> true
        | Error _
        | Ok() -> false

    [<Fact>]
    let ``Destination resolves inside the profile's live mirror`` () =
        within (fun setup ->
            let plan = writePlan setup (Path.Combine(setup.TargetDir, "restored.txt"))
            Assert.True(refused (RestoreRun.checkDestinations (store setup) plan)))

    [<Fact>]
    let ``Destination reached only through a reparse point into the mirror`` () =
        within (fun setup ->
            let junction = Path.Combine(setup.Root, "elsewhere")
            write setup "a.txt" "alpha" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore
            InteropTests.createJunction junction setup.TargetDir
            let destination = Path.Combine(junction, "sub", "restored.txt")
            let plan = planFor setup "a.txt" destination
            // Lexically outside the mirror; really inside it.
            Assert.False(destination.StartsWith(setup.TargetDir, StringComparison.OrdinalIgnoreCase))
            Assert.True(refused (RestoreRun.checkDestinations (store setup) plan))
            // Nothing was written, because the check refused before any write.
            Assert.False(File.Exists(Path.Combine(setup.TargetDir, "sub", "restored.txt"))))

    [<Fact>]
    let ``a destination outside the mirror is accepted`` () =
        within (fun setup ->
            let plan = writePlan setup (Path.Combine(setup.Root, "out", "restored.txt"))
            Assert.Equal(Ok(), RestoreRun.checkDestinations (store setup) plan))

    /// A real symlink needs the privilege (or Developer Mode) that creates one; where it cannot be made, the junction
    /// test above stands for the same code path (both are reparse points resolved by `Kernel32.resolveRealPath`).
    [<Fact>]
    let ``a symlink into the mirror is refused where one can be created`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            runOk quiet (profileOf setup "p" None) |> ignore
            let link = Path.Combine(setup.Root, "symlinked")

            let created =
                try
                    Directory.CreateSymbolicLink(link, setup.TargetDir) |> ignore
                    true
                with
                | :? IOException
                | :? UnauthorizedAccessException -> false

            if created then
                let plan = planFor setup "a.txt" (Path.Combine(link, "restored.txt"))
                Assert.True(refused (RestoreRun.checkDestinations (store setup) plan))
                Assert.False(File.Exists(Path.Combine(setup.TargetDir, "restored.txt"))))
