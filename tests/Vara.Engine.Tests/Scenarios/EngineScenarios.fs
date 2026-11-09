/// Backup engine scenarios written once against an `Engine`, a function that runs a backup of the rig's source into its
/// target. Each run is traced: what the run reported and what the target holds afterwards (manifest rows, mirror
/// contents, read-only flags, blobs, staging). The trace of a scenario is the text that is recorded from the C# engine
/// and compared with the F# engine's.
///
/// Source files are written through `write`, which gives each a modification time from a clock of the rig's own, so that
/// the rows (which keep the modification time) are the same in every run.
module Vara.Engine.Tests.EngineScenarios

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open Vara.Engine
open Vara.Engine.Tests
open Vara.Testing

type Rig =
    { Root: string
      Source: string
      Target: string
      /// The text of each run so far.
      Trace: ResizeArray<string>
      /// The next modification time to hand out, in seconds after the epoch below.
      Clock: int ref }

/// What a run reports, in a form both engines can fill.
type Observed =
    { Cancelled: bool
      Added: int
      Changed: int
      Moved: int
      Deleted: int
      Failed: int
      Bytes: int64
      FailedPaths: string list }

type Ended =
    | Returned of Observed
    | Threw of string list

/// Effects that make a run unusual, expressed once for each engine.
type Variation =
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

/// A backup of the rig's source into its target.
type Engine = Rig -> Variation -> Ended

/// A scenario: a name, which is also the name of its recorded file, and a body that runs the engine it is given.
type Scenario =
    { Name: string
      Body: Rig -> (Variation -> Ended) -> unit }

let plain =
    { StopRequested = false
      NoHardlinks = false
      HardlinkLimitOnce = false
      TearFile = None
      CrashOnRead = None
      TransferConcurrency = 1
      Excludes = []
      IncludeGlobs = []
      ExcludeGlobs = [] }

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

/// Runs the body in a fresh directory with an empty source.
let using (body: Rig -> 'T) : 'T =
    TempDir.using (fun root ->
        let rig =
            { Root = root
              Source = Path.Combine(root, "source")
              Target = Path.Combine(root, "target")
              Trace = ResizeArray()
              Clock = ref 0 }

        Directory.CreateDirectory rig.Source |> ignore
        body rig)

/// Writes a file and gives it the next modification time of the rig's clock.
let rewrite (rig: Rig) (path: string) (text: string) : unit =
    TempDir.write path text
    rig.Clock.Value <- rig.Clock.Value + 1
    File.SetLastWriteTimeUtc(path, epoch.AddSeconds(float rig.Clock.Value))

let write (rig: Rig) (name: string) (text: string) : string =
    let path = Path.Combine(rig.Source, name)
    rewrite rig path text
    path

let touch (path: string) : unit = File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours 3.0)

// ---- the F# engine -------------------------------------------------------------------------------------------

let private absolute (path: string) : Vara.Domain.AbsolutePath = (Vara.Domain.AbsolutePath.tryCreate path).Value

let runFs (rig: Rig) (variation: Variation) : Ended =
    let concurrency: Vara.Domain.Concurrency =
        { Scan = None
          Transfer = Vara.Domain.ConcurrencyLimit.tryCreate variation.TransferConcurrency }

    let source: Vara.Domain.Source =
        { Path = absolute rig.Source
          Recursive = true
          Excludes = variation.Excludes
          IncludeGlobs = variation.IncludeGlobs
          ExcludeGlobs = variation.ExcludeGlobs }

    let profile =
        match Vara.Domain.Profile.create "p" (absolute rig.Target) [ source ] None (Some concurrency) with
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
                { Cancelled = result.Status = Vara.Domain.SnapshotStatus.Cancelled
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
        use connection = new SqliteConnection $"Data Source={database};Pooling=False"
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        use reader = command.ExecuteReader()
        let result = ResizeArray()

        while reader.Read() do
            result.Add(String.Join("|", [ for i in 0 .. reader.FieldCount - 1 -> string (reader.GetValue i) ]))

        List.ofSeq result

let snapshotRows (target: string) : string list =
    rows
        target
        "SELECT status, bytes_transferred, files_added, files_changed, files_moved, files_deleted, files_failed FROM snapshots ORDER BY id"

let private versionRows (target: string) : string list =
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

let private describeEnded (ended: Ended) : string =
    match ended with
    | Returned observed ->
        let paths = String.Join("; ", observed.FailedPaths)

        $"returned cancelled={observed.Cancelled} added={observed.Added} changed={observed.Changed} moved={observed.Moved} deleted={observed.Deleted} failed={observed.Failed} bytes={observed.Bytes} failed-paths=[{paths}]"
    | Threw messages -> "threw " + String.Join("; ", messages)

/// The text of one run: how it ended and what the target holds.
let private describeRun (rig: Rig) (number: int) (ended: Ended) : string =
    let section (name: string) (items: string list) = $"{name}: {items.Length}" :: (items |> List.map (fun item -> "  " + item))

    let text =
        String.Join(
            "\n",
            [ yield $"run {number}: {describeEnded ended}"
              yield! section "snapshots" (snapshotRows rig.Target)
              yield! section "versions" (versionRows rig.Target)
              yield! section "mirror" (filesUnder rig.Target true)
              yield! section "blobs" (blobs rig.Target)
              yield! section "staging" (staging rig.Target) ]
        )

    Anonymize.anonymize rig.Root text

/// The engine as a function of the variation alone, adding the text of each run to the rig's trace.
let traced (engine: Engine) (rig: Rig) : Variation -> Ended =
    fun variation ->
        let ended = engine rig variation
        rig.Trace.Add(describeRun rig (rig.Trace.Count + 1) ended)
        ended

let returned (ended: Ended) : Observed =
    match ended with
    | Returned observed -> observed
    | Threw messages -> failwith (String.Join(", ", messages))

let private cmd (arguments: string) : int =
    let info = ProcessStartInfo("cmd.exe", $"/c {arguments}")
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    use proc = Process.Start info |> nonNull
    proc.WaitForExit()
    proc.ExitCode

let private changes (observed: Observed) : int = observed.Added + observed.Changed + observed.Moved + observed.Deleted

/// A database from before the case-insensitive collation, in the target, with one snapshot and one row.
let private legacyDatabase (target: string) : unit =
    let database = Path.Combine(target, ".vara", "profile.db")
    Directory.CreateDirectory(Path.GetDirectoryName database |> nonNull) |> ignore
    use connection = new SqliteConnection $"Data Source={database};Pooling=False"
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

let private scenario (name: string) (body: Rig -> (Variation -> Ended) -> unit) : Scenario = { Name = name; Body = body }

let private incremental (concurrency: int) (rig: Rig) (run: Variation -> Ended) : unit =
    let variation = { plain with TransferConcurrency = concurrency }
    let changed = write rig "changed.txt" "before"
    let moved = write rig "moved.txt" "moving content"
    let deleted = write rig "deleted.txt" "doomed"
    write rig "sub\\kept.txt" "kept" |> ignore
    write rig "same-size-a.txt" "123456" |> ignore
    write rig "empty.txt" "" |> ignore

    let first = returned (run variation)
    Assert.Equal(6, first.Added)

    rewrite rig changed "after, longer"
    File.Move(moved, Path.Combine(rig.Source, "sub", "renamed.txt"))
    File.Delete deleted
    write rig "added.txt" "new" |> ignore
    let second = returned (run variation)
    Assert.Equal((1, 1, 1, 1), (second.Added, second.Changed, second.Moved, second.Deleted))

    // Nothing changed: nothing to do, and still identical.
    let third = returned (run variation)
    Assert.Equal(0, changes third)

let scenarios: Scenario list =
    [ scenario "first-and-incremental-backups-concurrency-1" (incremental 1)
      scenario "first-and-incremental-backups-concurrency-4" (incremental 4)

      scenario "duplicates-moves-and-a-file-replaced-by-a-link" (fun rig run ->
          write rig "dup1.txt" "duplicate content" |> ignore
          write rig "dup2.txt" "duplicate content" |> ignore
          write rig "dup3.txt" "duplicate content" |> ignore
          let tracked = write rig "becomes-link" "regular content"
          run plain |> ignore

          // Two of the three duplicates move; their same-size candidates are consumed in the manifest's order.
          File.Move(Path.Combine(rig.Source, "dup1.txt"), Path.Combine(rig.Source, "moved1.txt"))
          File.Move(Path.Combine(rig.Source, "dup3.txt"), Path.Combine(rig.Source, "moved3.txt"))
          let outside = Path.Combine(rig.Root, "outside")
          Directory.CreateDirectory outside |> ignore
          File.Delete tracked
          InteropTests.createJunction tracked outside
          run plain |> ignore

          // The link is unchanged on the next run.
          Assert.Equal(0, changes (returned (run plain))))

      scenario "excludes-globs-and-includes" (fun rig run ->
          write rig "keep.txt" "keep" |> ignore
          write rig "notes.md" "notes" |> ignore
          write rig "build\\cache\\skip.txt" "skip" |> ignore
          write rig "logs\\old\\skip.log" "skip" |> ignore

          let variation =
              { plain with
                  Excludes = [ "build/cache"; "logs\\old" ]
                  ExcludeGlobs = [ "**/*.md" ] }

          run variation |> ignore
          write rig "extra.txt" "extra" |> ignore
          run { plain with IncludeGlobs = [ "**/*.txt" ] } |> ignore)

      scenario "a-source-that-no-longer-exists" (fun rig run ->
          write rig "a.txt" "alpha" |> ignore
          run plain |> ignore
          Directory.Delete(rig.Source, true)
          let ended = returned (run plain)
          Assert.Equal(1, ended.Failed)
          Assert.Equal(0, ended.Deleted))

      scenario "a-locked-file" (fun rig run ->
          let locked = write rig "locked.txt" "locked"
          write rig "fine.txt" "fine" |> ignore
          use _hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)
          Assert.Equal(1, (returned (run plain)).Failed))

      scenario "an-unreadable-directory" (fun rig run ->
          write rig "sub\\recorded.txt" "recorded" |> ignore
          write rig "other.txt" "other" |> ignore
          run plain |> ignore
          let denied = Path.Combine(rig.Source, "sub")
          cmd $"icacls \"{denied}\" /deny \"%%USERNAME%%:(RD)\"" |> ignore

          try
              let ended = returned (run plain)
              Assert.Equal(0, ended.Deleted)
              Assert.Equal(1, ended.Failed)
          finally
              cmd $"icacls \"{denied}\" /remove:d \"%%USERNAME%%\"" |> ignore)

      scenario "a-file-modified-during-its-own-transfer" (fun rig run ->
          let torn = write rig "torn.txt" "torn content"
          write rig "fine.txt" "fine" |> ignore
          let ended = returned (run { plain with TearFile = Some torn })
          Assert.Equal(1, ended.Failed)
          Assert.Equal(1, ended.Added))

      scenario "a-target-without-hard-link-support" (fun rig run ->
          write rig "a.txt" "alpha" |> ignore
          write rig "b.txt" "alpha" |> ignore
          Assert.Equal(2, (returned (run { plain with NoHardlinks = true })).Added))

      scenario "a-blob-at-the-hard-link-limit" (fun rig run ->
          write rig "a.txt" "alpha" |> ignore
          write rig "b.txt" "alpha" |> ignore
          write rig "c.txt" "gamma" |> ignore
          let ended = returned (run { plain with HardlinkLimitOnce = true })
          Assert.Equal(3, ended.Added)
          Assert.Equal(0, ended.Failed))

      scenario "cancellation-before-any-operation" (fun rig run ->
          write rig "a.txt" "alpha" |> ignore
          let ended = returned (run { plain with StopRequested = true })
          Assert.True ended.Cancelled
          Assert.Equal(0, ended.Added))

      scenario "a-crash-in-the-last-transfer" (fun rig run ->
          write rig "a.txt" "alpha" |> ignore
          write rig "b.txt" "beta" |> ignore
          write rig "c.txt" "gamma" |> ignore

          match run { plain with CrashOnRead = Some 3 } with
          | Threw messages -> Assert.Equal<string list>([ "boom" ], messages)
          | Returned _ -> Assert.Fail "expected the run to throw"

          // The snapshot is recorded as failed, and the two files before the crash were recorded.
          Assert.Equal<string list>([ "Failed|0|0|0|0|0|0" ], snapshotRows rig.Target))

      scenario "a-database-from-before-the-case-insensitive-collation" (fun rig run ->
          write rig "a.txt" "alpha" |> ignore
          legacyDatabase rig.Target
          let ended = returned (run plain)
          Assert.Equal(1, ended.Added)
          Assert.Equal(1, ended.Deleted)) ]

/// The trace of a scenario, run on a fresh rig with the engine.
let observe (engine: Engine) (scenario: Scenario) : string =
    using (fun rig ->
        scenario.Body rig (traced engine rig)
        String.Join("\n", rig.Trace))
