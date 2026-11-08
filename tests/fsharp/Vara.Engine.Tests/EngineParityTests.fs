/// Runs the C# backup engine and the F# one on the same sources and compares what they leave behind: manifest rows,
/// snapshots, mirror contents, read-only flags and blobs. Removed at the cutover, with the C# engine.
module Vara.Engine.Tests.EngineParityTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open Vara.Engine
open Vara.Engine.Tests

type private Cs = Vara.Core.Configuration.Source
type private CsProfile = Vara.Core.Configuration.Profile
type private CsConcurrency = Vara.Core.Configuration.ConcurrencySettings
type private FsSource = Vara.Domain.Source

type private Rig =
    { Root: string
      Source: string
      CsTarget: string
      FsTarget: string }

/// What a run reports, in a form both engines can fill.
type private Observed =
    { Cancelled: bool
      Added: int
      Changed: int
      Moved: int
      Deleted: int
      Failed: int
      Bytes: int64
      FailedPaths: string list }

type private Ended =
    | Returned of Observed
    | Threw of string list

/// Effects that make a run unusual, expressed once for each engine.
type private Variation =
    { /// The run starts with a graceful stop already requested.
      StopRequested: bool
      /// The target has no hard-link support.
      NoHardlinks: bool
      /// The first hard-link attempt of a placement fails, as at the hard-link limit.
      HardlinkLimitOnce: bool
      /// The source file gets a new modification time during its transfer.
      TearFile: string option
      /// The n-th file read for transfer raises an unexpected exception (1-based).
      CrashOnRead: int option
      TransferConcurrency: int
      Excludes: string list
      IncludeGlobs: string list
      ExcludeGlobs: string list }

let private plain =
    { StopRequested = false
      NoHardlinks = false
      HardlinkLimitOnce = false
      TearFile = None
      CrashOnRead = None
      TransferConcurrency = 1
      Excludes = []
      IncludeGlobs = []
      ExcludeGlobs = [] }

let private withRig (body: Rig -> unit) =
    TempDir.using (fun root ->
        let rig =
            { Root = root
              Source = Path.Combine(root, "source")
              CsTarget = Path.Combine(root, "cs-target")
              FsTarget = Path.Combine(root, "fs-target") }

        Directory.CreateDirectory rig.Source |> ignore
        body rig)

let private write (rig: Rig) (name: string) (text: string) : string =
    let path = Path.Combine(rig.Source, name)
    TempDir.write path text
    path

// ---- the C# engine -------------------------------------------------------------------------------------------

let private csField (name: string) : FieldInfo =
    typeof<Vara.Infrastructure.Storage.FileSystemContentStore>
        .GetField(name, BindingFlags.NonPublic ||| BindingFlags.Instance)
    |> nonNull

/// The C# store behind the interface, with the variations the C# test hooks cannot reach from here.
let private csStore (target: string) (variation: Variation) : Vara.Core.Abstractions.IContentStore =
    let inner = new Vara.Infrastructure.Storage.FileSystemContentStore(target, Vara.Infrastructure.Hashing.XxHash128Hasher())

    if variation.HardlinkLimitOnce then
        (csField "_forceNextHardlinkFailureForTesting").SetValue(inner, true)

    let stores = ref 0

    { new Vara.Core.Abstractions.IContentStore with
        member _.ProbeHardlinkSupport() =
            let supported = inner.ProbeHardlinkSupport()

            if variation.NoHardlinks then
                (csField "_supportsHardlinks").SetValue(inner, Nullable false)
                false
            else
                supported

        member _.SupportsHardlinks = if variation.NoHardlinks then false else inner.SupportsHardlinks

        member _.StoreFromStream(content, onBytesWritten) =
            match variation.CrashOnRead with
            | Some n when Interlocked.Increment(&stores.contents) = n -> raise (InvalidOperationException "boom")
            | _ -> inner.StoreFromStream(content, onBytesWritten)

        member _.HasContent hash = inner.HasContent hash
        member _.PlaceAtMirrorPath(hash, path, onBytes, previous) = inner.PlaceAtMirrorPath(hash, path, onBytes, previous)
        member _.MoveMirrorEntry(fromPath, toPath) = inner.MoveMirrorEntry(fromPath, toPath)
        member _.RemoveFromMirror(path, hash) = inner.RemoveFromMirror(path, hash)
        member _.DeleteContent hash = inner.DeleteContent hash
        member _.CleanupOrphanedTemp() = inner.CleanupOrphanedTemp()
        member _.ExtractTo(hash, destination, onBytes) = inner.ExtractTo(hash, destination, onBytes)
        member _.RemoveExtractedFile path = inner.RemoveExtractedFile path
        member _.OpenRead hash = inner.OpenRead hash
        member _.IsWithinMirror path = inner.IsWithinMirror path
        member _.TargetExists path = inner.TargetExists path
        member _.ListAllStoredHashes() = inner.ListAllStoredHashes() }

let private touch (path: string) = File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours 3.0)

/// The scanner, then a change to the file's modification time once the scan is over: a torn transfer.
let private csScanner (variation: Variation) : Vara.Core.Abstractions.IFileSystemScanner =
    let real = Vara.Infrastructure.FileSystem.DirectoryFileSystemScanner()

    { new Vara.Core.Abstractions.IFileSystemScanner with
        member _.Scan sources =
            let result = real.Scan sources
            let entries = Seq.toList result.Entries

            match variation.TearFile with
            | Some path -> touch path
            | None -> ()

            Vara.Core.Abstractions.ScanResult(entries, result.Failures) }

let private runCs (rig: Rig) (variation: Variation) : Ended =
    let profile =
        CsProfile(
            "p",
            rig.CsTarget,
            [ Cs(rig.Source, true, variation.Excludes, variation.IncludeGlobs, variation.ExcludeGlobs) ],
            null,
            CsConcurrency(Nullable(), Nullable variation.TransferConcurrency)
        )

    let database = Path.Combine(rig.CsTarget, ".vara", "profile.db")
    Directory.CreateDirectory(Path.GetDirectoryName database |> nonNull) |> ignore
    use repository = new Vara.Infrastructure.Snapshots.SqliteSnapshotRepository(database)
    let hasher = Vara.Infrastructure.Hashing.XxHash128Hasher()
    use lockHandle = new Vara.Infrastructure.Concurrency.FileRunLock()

    let pipeline =
        Vara.Application.Backup.BackupPipeline(
            csScanner variation,
            hasher,
            csStore rig.CsTarget variation,
            repository,
            lockHandle,
            IO.TextWriter.Null
        )

    use cancellation = new CancellationTokenSource()

    if variation.StopRequested then
        cancellation.Cancel()

    try
        let result = pipeline.RunAsync(profile, null, null, cancellation.Token).GetAwaiter().GetResult()
        let stats = result.Stats

        Returned
            { Cancelled = result.Cancelled
              Added = stats.FilesAdded
              Changed = stats.FilesChanged
              Moved = stats.FilesMoved
              Deleted = stats.FilesDeleted
              Failed = stats.FilesFailed
              Bytes = stats.BytesTransferred
              FailedPaths = result.FailedPaths |> Seq.sort |> List.ofSeq }
    with
    | :? AggregateException as aggregate -> Threw [ for inner in aggregate.InnerExceptions -> inner.Message ]
    | error -> Threw [ error.Message ]

// ---- the F# engine -------------------------------------------------------------------------------------------

let private absolute (path: string) : Vara.Domain.AbsolutePath = (Vara.Domain.AbsolutePath.tryCreate path).Value

let private runFs (rig: Rig) (variation: Variation) : Ended =
    let concurrency: Vara.Domain.Concurrency =
        { Scan = None
          Transfer = Vara.Domain.ConcurrencyLimit.tryCreate variation.TransferConcurrency }

    let source: FsSource =
        { Path = absolute rig.Source
          Recursive = true
          Excludes = variation.Excludes
          IncludeGlobs = variation.IncludeGlobs
          ExcludeGlobs = variation.ExcludeGlobs }

    let profile =
        match Vara.Domain.Profile.create "p" (absolute rig.FsTarget) [ source ] None (Some concurrency) with
        | Ok profile -> profile
        | Error _ -> failwith "invalid profile"

    let reads = ref 0
    let linkAttempts = ref 0

    let links: Links =
        { Create =
            fun newLink existing ->
                // The probe makes its own throwaway link first; only placements count as attempts.
                let isProbe = newLink.Contains "probe-link-"

                if variation.NoHardlinks then
                    raise (IOException "no hard links on this volume")
                elif variation.HardlinkLimitOnce && not isProbe && Interlocked.Increment(&linkAttempts.contents) = 1 then
                    raise (IOException "hard-link limit reached")
                else
                    Kernel32.createHardLink newLink existing }

    let openRead (path: string) : Stream =
        match variation.CrashOnRead with
        | Some n when Interlocked.Increment(&reads.contents) = n -> raise (InvalidOperationException "boom")
        | _ -> ()

        let stream = File.OpenRead path

        match variation.TearFile with
        | Some torn when String.Equals(torn, path, StringComparison.OrdinalIgnoreCase) -> touch path
        | _ -> ()

        stream :> Stream

    let env =
        { BackupRun.live (fun () -> variation.StopRequested) with
            Links = links
            OpenRead = openRead
            Diagnostics = ignore }

    try
        match (BackupRun.run env profile).GetAwaiter().GetResult() with
        | Ok result ->
            let totals = result.Totals

            Returned
                { Cancelled = (result.Status = Vara.Domain.SnapshotStatus.Cancelled)
                  Added = totals.FilesAdded
                  Changed = totals.FilesChanged
                  Moved = totals.FilesMoved
                  Deleted = totals.FilesDeleted
                  Failed = totals.FilesFailed
                  Bytes = totals.BytesTransferred
                  FailedPaths = result.FailedPaths |> List.sort }
        | Error refusal -> Threw [ Vara.Domain.BackupRefusal.render refusal ]
    with
    | :? AggregateException as aggregate -> Threw [ for inner in aggregate.InnerExceptions -> inner.Message ]
    | error -> Threw [ error.Message ]

// ---- what a target holds -------------------------------------------------------------------------------------

let private rows (target: string) (sql: string) : string list =
    let database = Path.Combine(target, ".vara", "profile.db")

    if not (File.Exists database) then
        []
    else
        SqliteConnection.ClearAllPools()
        use connection = new SqliteConnection($"Data Source={database};Pooling=False")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        use reader = command.ExecuteReader()
        let result = ResizeArray()

        while reader.Read() do
            result.Add(String.Join("|", [ for i in 0 .. reader.FieldCount - 1 -> string (reader.GetValue i) ]))

        List.ofSeq result

let private snapshotRows (target: string) =
    rows
        target
        "SELECT status, bytes_transferred, files_added, files_changed, files_moved, files_deleted, files_failed FROM snapshots ORDER BY id"

let private versionRows (target: string) =
    rows
        target
        "SELECT snapshot_id, relative_path, ifnull(previous_relative_path,'~'), ifnull(content_hash,'~'), size, source_modified_at, change_kind, ifnull(quick_hash,'~'), ifnull(quick_hash_scheme,'~'), ifnull(link_target,'~') FROM file_versions"
    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

let private filesUnder (root: string) (excludeVara: bool) : string list =
    if not (Directory.Exists root) then
        []
    else
        [ for file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
              let relative = Path.GetRelativePath(root, file)

              if excludeVara = not (relative.StartsWith ".vara") then
                  let attributes = File.GetAttributes file
                  let content = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes file))
                  $"{relative}|{content}|readonly={attributes.HasFlag FileAttributes.ReadOnly}" ]
        |> List.sortWith (fun a b -> String.Compare(a, b, StringComparison.OrdinalIgnoreCase))

let private mirrorContents (target: string) : string list = filesUnder target true

let private blobs (target: string) : string list =
    let versions = Path.Combine(target, ".vara", "versions")

    if not (Directory.Exists versions) then
        []
    else
        [ for file in Directory.EnumerateFiles(versions, "*", SearchOption.AllDirectories) ->
              let attributes = File.GetAttributes file
              $"{Path.GetFileName file}|{FileInfo(file).Length}|readonly={attributes.HasFlag FileAttributes.ReadOnly}" ]
        |> List.sort

let private staging (target: string) : string list =
    let temp = Path.Combine(target, ".vara", "tmp")
    if Directory.Exists temp then Directory.EnumerateFiles temp |> Seq.map Path.GetFileName |> List.ofSeq else []

let private assertSameEnding (cs: Ended) (fs: Ended) =
    match cs, fs with
    | Returned a, Returned b -> Assert.Equal(a, b)
    | Threw a, Threw b -> Assert.Equal<string list>(a, b)
    | _ -> Assert.Fail $"the engines ended differently: C# {cs}, F# {fs}"

let private assertSameTargets (rig: Rig) =
    Assert.Equal<string list>(snapshotRows rig.CsTarget, snapshotRows rig.FsTarget)
    Assert.Equal<string list>(versionRows rig.CsTarget, versionRows rig.FsTarget)
    Assert.Equal<string list>(mirrorContents rig.CsTarget, mirrorContents rig.FsTarget)
    Assert.Equal<string list>(blobs rig.CsTarget, blobs rig.FsTarget)
    Assert.Equal<string list>(staging rig.CsTarget, staging rig.FsTarget)

/// Runs both engines against the current source and requires the same ending and the same targets.
let private bothAgree (rig: Rig) (variation: Variation) : Ended =
    let cs = runCs rig variation
    let fs = runFs rig variation
    assertSameEnding cs fs
    assertSameTargets rig
    cs

let private cmd (arguments: string) : int =
    let info = ProcessStartInfo("cmd.exe", $"/c {arguments}")
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    use proc = Process.Start info |> nonNull
    proc.WaitForExit()
    proc.ExitCode

let private returned (ended: Ended) : Observed =
    match ended with
    | Returned observed -> observed
    | Threw messages -> failwith (String.Join(", ", messages))

module ``First and incremental backups`` =
    [<Theory>]
    [<InlineData(1)>]
    [<InlineData(4)>]
    let ``change, move, delete and add leave identical targets`` (concurrency: int) =
        withRig (fun rig ->
            let variation = { plain with TransferConcurrency = concurrency }
            let changed = write rig "changed.txt" "before"
            let moved = write rig "moved.txt" "moving content"
            let deleted = write rig "deleted.txt" "doomed"
            write rig "sub\\kept.txt" "kept" |> ignore
            write rig "same-size-a.txt" "123456" |> ignore
            write rig "empty.txt" "" |> ignore

            let first = returned (bothAgree rig variation)
            Assert.Equal(6, first.Added)

            File.WriteAllText(changed, "after, longer")
            File.Move(moved, Path.Combine(rig.Source, "sub", "renamed.txt"))
            File.Delete deleted
            write rig "added.txt" "new" |> ignore
            let second = returned (bothAgree rig variation)
            Assert.Equal((1, 1, 1, 1), (second.Added, second.Changed, second.Moved, second.Deleted))

            // Nothing changed: nothing to do, and still identical.
            let third = returned (bothAgree rig variation)
            Assert.Equal(0, third.Added + third.Changed + third.Moved + third.Deleted))

    [<Fact>]
    let ``identical content in several files, a move among duplicates, and a file replaced by a link`` () =
        withRig (fun rig ->
            write rig "dup1.txt" "duplicate content" |> ignore
            write rig "dup2.txt" "duplicate content" |> ignore
            write rig "dup3.txt" "duplicate content" |> ignore
            let tracked = write rig "becomes-link" "regular content"
            bothAgree rig plain |> ignore

            // Two of the three duplicates move; their same-size candidates are consumed in the manifest's order.
            File.Move(Path.Combine(rig.Source, "dup1.txt"), Path.Combine(rig.Source, "moved1.txt"))
            File.Move(Path.Combine(rig.Source, "dup3.txt"), Path.Combine(rig.Source, "moved3.txt"))
            let outside = Path.Combine(rig.Root, "outside")
            Directory.CreateDirectory outside |> ignore
            File.Delete tracked
            InteropTests.createJunction tracked outside
            bothAgree rig plain |> ignore

            // The link is unchanged on the next run.
            let again = returned (bothAgree rig plain)
            Assert.Equal(0, again.Added + again.Changed + again.Moved + again.Deleted))

module ``Scanner rules`` =
    [<Fact>]
    let ``excludes in both separator styles, globs, and an unavailable source`` () =
        withRig (fun rig ->
            write rig "keep.txt" "keep" |> ignore
            write rig "notes.md" "notes" |> ignore
            write rig "build\\cache\\skip.txt" "skip" |> ignore
            write rig "logs\\old\\skip.log" "skip" |> ignore
            let variation =
                { plain with
                    Excludes = [ "build/cache"; "logs\\old" ]
                    ExcludeGlobs = [ "**/*.md" ] }

            bothAgree rig variation |> ignore

            let included = { plain with IncludeGlobs = [ "**/*.txt" ] }
            write rig "extra.txt" "extra" |> ignore
            bothAgree rig included |> ignore)

    [<Fact>]
    let ``a source that no longer exists is reported and its files are kept`` () =
        withRig (fun rig ->
            write rig "a.txt" "alpha" |> ignore
            bothAgree rig plain |> ignore
            Directory.Delete(rig.Source, true)
            let ended = returned (bothAgree rig plain)
            Assert.Equal(1, ended.Failed)
            Assert.Equal(0, ended.Deleted))

module ``Failure paths`` =
    [<Fact>]
    let ``a locked file`` () =
        withRig (fun rig ->
            let locked = write rig "locked.txt" "locked"
            write rig "fine.txt" "fine" |> ignore
            use hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)
            let ended = returned (bothAgree rig plain)
            Assert.Equal(1, ended.Failed))

    [<Fact>]
    let ``an unreadable directory suppresses the deletion of what was recorded under it`` () =
        withRig (fun rig ->
            write rig "sub\\recorded.txt" "recorded" |> ignore
            write rig "other.txt" "other" |> ignore
            bothAgree rig plain |> ignore
            let denied = Path.Combine(rig.Source, "sub")
            cmd $"icacls \"{denied}\" /deny \"%%USERNAME%%:(RD)\"" |> ignore

            try
                let ended = returned (bothAgree rig plain)
                Assert.Equal(0, ended.Deleted)
                Assert.Equal(1, ended.Failed)
            finally
                cmd $"icacls \"{denied}\" /remove:d \"%%USERNAME%%\"" |> ignore)

    [<Fact>]
    let ``a file modified during its own transfer`` () =
        withRig (fun rig ->
            let torn = write rig "torn.txt" "torn content"
            write rig "fine.txt" "fine" |> ignore
            let ended = returned (bothAgree rig { plain with TearFile = Some torn })
            Assert.Equal(1, ended.Failed)
            Assert.Equal(1, ended.Added))

    [<Fact>]
    let ``a target without hard-link support`` () =
        withRig (fun rig ->
            write rig "a.txt" "alpha" |> ignore
            write rig "b.txt" "alpha" |> ignore
            let variation = { plain with NoHardlinks = true }
            let ended = returned (bothAgree rig variation)
            Assert.Equal(2, ended.Added))

    [<Fact>]
    let ``a blob that has reached the hard-link limit`` () =
        withRig (fun rig ->
            write rig "a.txt" "alpha" |> ignore
            write rig "b.txt" "alpha" |> ignore
            write rig "c.txt" "gamma" |> ignore
            let ended = returned (bothAgree rig { plain with HardlinkLimitOnce = true })
            Assert.Equal(3, ended.Added)
            Assert.Equal(0, ended.Failed))

    [<Fact>]
    let ``cancellation before any operation`` () =
        withRig (fun rig ->
            write rig "a.txt" "alpha" |> ignore
            let ended = returned (bothAgree rig { plain with StopRequested = true })
            Assert.True ended.Cancelled
            Assert.Equal(0, ended.Added))

    [<Fact>]
    let ``a crash in the last transfer`` () =
        withRig (fun rig ->
            write rig "a.txt" "alpha" |> ignore
            write rig "b.txt" "beta" |> ignore
            write rig "c.txt" "gamma" |> ignore

            match bothAgree rig { plain with CrashOnRead = Some 3 } with
            | Threw messages -> Assert.Equal<string list>([ "boom" ], messages)
            | Returned _ -> Assert.Fail "expected the run to throw"

            // The snapshot is recorded as failed, and the two files before the crash were recorded.
            Assert.Equal<string list>([ "Failed|0|0|0|0|0|0" ], snapshotRows rig.FsTarget))

    [<Fact>]
    let ``a database from before the case-insensitive collation`` () =
        withRig (fun rig ->
            write rig "a.txt" "alpha" |> ignore

            for target in [ rig.CsTarget; rig.FsTarget ] do
                let database = Path.Combine(target, ".vara", "profile.db")
                Directory.CreateDirectory(Path.GetDirectoryName database |> nonNull) |> ignore
                use connection = new SqliteConnection($"Data Source={database};Pooling=False")
                connection.Open()
                use command = connection.CreateCommand()

                command.CommandText <-
                    """
                    CREATE TABLE snapshots (id INTEGER PRIMARY KEY AUTOINCREMENT, started_at TEXT NOT NULL, completed_at TEXT NULL,
                        status TEXT NOT NULL, bytes_transferred INTEGER NOT NULL DEFAULT 0, files_added INTEGER NOT NULL DEFAULT 0,
                        files_changed INTEGER NOT NULL DEFAULT 0, files_moved INTEGER NOT NULL DEFAULT 0,
                        files_deleted INTEGER NOT NULL DEFAULT 0, files_failed INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE file_versions (id INTEGER PRIMARY KEY AUTOINCREMENT, snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
                        relative_path TEXT NOT NULL, previous_relative_path TEXT NULL, content_hash TEXT NULL, size INTEGER NOT NULL,
                        source_modified_at TEXT NOT NULL, change_kind TEXT NOT NULL, recorded_at TEXT NOT NULL,
                        quick_hash TEXT NULL, quick_hash_scheme INTEGER NULL);
                    INSERT INTO snapshots (started_at, status) VALUES ('2026-01-01T00:00:00.0000000+00:00', 'Complete');
                    INSERT INTO file_versions (snapshot_id, relative_path, content_hash, size, source_modified_at, change_kind, recorded_at)
                        VALUES (1, 'X\gone.txt', 'aa11', 3, '2026-01-01T00:00:00.0000000+00:00', 'Added', '2026-01-01T00:00:00.0000000+00:00');
                    """

                command.ExecuteNonQuery() |> ignore

            let ended = returned (bothAgree rig plain)
            Assert.Equal(1, ended.Added)
            Assert.Equal(1, ended.Deleted))

module ``Cross-implementation continuation`` =
    [<Fact>]
    let ``the F# engine continues a target the C# engine wrote without re-transfers`` () =
        withRig (fun rig ->
            write rig "a.txt" "alpha" |> ignore
            write rig "sub\\b.txt" "beta" |> ignore
            let first = returned (runCs rig plain)
            Assert.Equal(2, first.Added)

            // The F# engine runs against the C# target.
            let continued = { rig with FsTarget = rig.CsTarget }
            let second = returned (runFs continued plain)
            Assert.Equal(0, second.Added + second.Changed + second.Moved + second.Deleted)

            // And it carries on from there: a change is picked up by either engine.
            write rig "c.txt" "gamma" |> ignore
            let third = returned (runFs continued plain)
            Assert.Equal(1, third.Added)
            let fourth = returned (runCs rig plain)
            Assert.Equal(0, fourth.Added + fourth.Changed + fourth.Moved + fourth.Deleted))

    [<Fact>]
    let ``the C# engine continues a target the F# engine wrote without re-transfers`` () =
        withRig (fun rig ->
            write rig "a.txt" "alpha" |> ignore
            write rig "sub\\b.txt" "beta" |> ignore
            let first = returned (runFs rig plain)
            Assert.Equal(2, first.Added)

            let continued = { rig with CsTarget = rig.FsTarget }
            let second = returned (runCs continued plain)
            Assert.Equal(0, second.Added + second.Changed + second.Moved + second.Deleted)

            // A moved file keeps its history across the implementations.
            File.Move(Path.Combine(rig.Source, "a.txt"), Path.Combine(rig.Source, "moved.txt"))
            let third = returned (runCs continued plain)
            Assert.Equal(1, third.Moved)
            let fourth = returned (runFs rig plain)
            Assert.Equal(0, fourth.Added + fourth.Changed + fourth.Moved + fourth.Deleted))
