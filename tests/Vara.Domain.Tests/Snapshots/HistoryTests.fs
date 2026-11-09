/// Tests of the pure history model. Each module is named after a requirement of
/// openspec/specs/snapshot-history/spec.md, backup-browsing/spec.md or cli-presentation/spec.md, and each test after
/// one of its scenarios; the commands' scenarios are tested again, through the command, in the command tests.
module Vara.Domain.Tests.HistoryTests

open System
open System.Text.RegularExpressions
open Xunit
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain
open Vara.Domain.Tests.Fixtures

let private show (result: Result<'a, HistoryError>) : string =
    match result with
    | Ok _ -> "Ok"
    | Error error -> HistoryError.render error

/// A row of the manifest: its version id, the snapshot that recorded it, the path, the kind of change, the minute it
/// was recorded and its size.
let row (id: int64) (snapshot: int64) (path: string) (kind: ChangeKind) (minutes: int) (size: int64) : FileVersion =
    { VersionId = id
      InSnapshot = snapshot
      Row =
        { RowPath = mirror path
          PreviousPath = ValueNone
          Content = RowContent.Regular(content $"hash{id}" size minutes)
          Kind = kind
          RecordedAt = at minutes } }

let private moved (id: int64) (snapshot: int64) (path: string) (previous: string) (minutes: int) : FileVersion =
    let version = row id snapshot path ChangeKind.Moved minutes 5L

    { version with Row.PreviousPath = ValueSome(mirror previous) }

let private linked (id: int64) (snapshot: int64) (path: string) (minutes: int) : FileVersion =
    let version = row id snapshot path ChangeKind.Linked minutes 0L

    { version with Row.Content = RowContent.Linked(ValueSome "C\\target") }

let private snapshot (id: int64) (minutes: int) (status: SnapshotStatus) : SnapshotInfo =
    { SnapshotId = id
      StartedAt = at minutes
      CompletedAt = ValueSome(at (minutes + 1))
      Status = status
      Stats =
        { FilesAdded = 0
          FilesChanged = 0
          FilesMoved = 0
          FilesDeleted = 0
          FilesFailed = 0
          BytesTransferred = 0L } }

let private okOrFail (result: Result<'a, HistoryError>) : 'a =
    match result with
    | Ok value -> value
    | Error error -> failwith (HistoryError.render error)

let private plain (display: Display) : string = Display.plainLines display |> String.concat "
"

let private names (entries: Entry list) : string list = entries |> List.map _.Name

module ``List snapshots`` =
    let private withStats (info: SnapshotInfo) (added: int) (bytes: int64) : SnapshotInfo =
        { info with Stats = { info.Stats with FilesAdded = added; BytesTransferred = bytes } }

    let private snapshots =
        [ withStats (snapshot 1L 10 SnapshotStatus.Complete) 5 1_000L
          withStats (snapshot 2L 20 SnapshotStatus.Failed) 12345 2_000_000L
          withStats (snapshot 3L 30 SnapshotStatus.Cancelled) 7 10L
          withStats (snapshot 4L 40 SnapshotStatus.Running) 0 0L ]

    let private table (display: Display) : Table =
        display |> List.pick (function Line.Table table -> Some table | _ -> None)

    [<Fact>]
    let ``Listing snapshot history`` () =
        let rows = (table (HistoryView.ofSnapshots snapshots)).Rows
        Assert.Equal(4, rows.Length)
        // Newest first, with the id and the start time in the first columns.
        Assert.Equal("4", rows.Head.Head.Text)
        Assert.Contains("2026-01-01 00:10:00", List.last rows |> List.map _.Text |> String.concat " ")

    [<Fact>]
    let ``No snapshots recorded`` () =
        Assert.Equal<string list>([ "No snapshots recorded yet." ], Display.plainLines (HistoryView.ofSnapshots []))

    [<Fact>]
    let ``Snapshot status reflects outcome severity`` () =
        let statusTones = (table (HistoryView.ofSnapshots snapshots)).Rows |> List.map (fun row -> row[2].Tone)
        Assert.Equal<Tone list>([ Tone.Neutral; Tone.PartialFailure; Tone.Error; Tone.Success ], statusTones)

    [<Fact>]
    let ``Cancelled run is distinguished from a failed run`` () =
        Assert.Equal(Tone.PartialFailure, HistoryView.toneOfStatus SnapshotStatus.Cancelled)
        Assert.Equal(Display.toneOf Outcome.PartialFailure, HistoryView.toneOfStatus SnapshotStatus.Cancelled)
        Assert.Equal(Tone.Error, HistoryView.toneOfStatus SnapshotStatus.Failed)

    [<Fact>]
    let ``Numeric statistics are right-aligned`` () =
        let shown = table (HistoryView.ofSnapshots snapshots)
        let numeric = shown.Columns |> List.skip 3
        Assert.True(numeric |> List.forall (fun column -> column.Align = Align.Right))
        // The digits line up: every row of the drawn table is as wide as the others, and "5" ends where "12345" does.
        let lines = Display.plainLines (HistoryView.ofSnapshots snapshots)
        Assert.Equal(1, lines |> List.map String.length |> List.distinct |> List.length)
        Assert.Contains("│     5 │", lines |> String.concat "
")

module ``File version history`` =
    let private rowsOf (all: FileVersion list) (path: MirrorPath) : FileVersion list =
        all |> List.filter (fun version -> String.Equals(MirrorPath.value version.Row.RowPath, MirrorPath.value path, StringComparison.OrdinalIgnoreCase))

    [<Fact>]
    let ``Viewing a file's history`` () =
        let all =
            [ row 1L 1L "C\\a.txt" ChangeKind.Added 10 5L
              row 2L 2L "C\\other.txt" ChangeKind.Added 20 5L
              row 3L 3L "C\\a.txt" ChangeKind.Changed 30 6L ]

        let history = History.versionsOf (rowsOf all) (mirror "C\\a.txt")
        Assert.Equal<int64 list>([ 3L; 1L ], history |> List.map _.VersionId)

    [<Fact>]
    let ``the history follows the move chain back through earlier paths`` () =
        let all =
            [ row 1L 1L "C\\old.txt" ChangeKind.Added 10 5L
              row 2L 2L "C\\old.txt" ChangeKind.Deleted 20 5L
              moved 3L 2L "C\\new.txt" "C\\old.txt" 20
              row 4L 3L "C\\new.txt" ChangeKind.Changed 30 6L ]

        let history = History.versionsOf (rowsOf all) (mirror "C\\new.txt")
        Assert.Equal<int64 list>([ 4L; 3L; 2L; 1L ], history |> List.map _.VersionId)

    [<Fact>]
    let ``a path nothing was recorded for has no history`` () =
        Assert.Empty(History.versionsOf (fun _ -> []) (mirror "C\\none.txt"))

    /// Rows whose moves point at each other in a cycle: the walk must still end, visiting each path once.
    [<Property>]
    let ``a move chain visits each path once`` () =
        let gen =
            gen {
                let! count = Gen.choose (1, 6)
                let! previous = Gen.listOfLength count (Gen.choose (0, 5))
                return previous
            }

        Prop.forAll (Arb.fromGen gen) (fun previous ->
            let pathAt (index: int) = $"C\\p{index % 6}.txt"

            let all =
                previous
                |> List.mapi (fun index target -> moved (int64 (index + 1)) 1L (pathAt index) (pathAt target) (index + 1))

            let asked = ResizeArray<string>()

            let lookup (path: MirrorPath) =
                asked.Add((MirrorPath.value path).ToLowerInvariant())
                rowsOf all path

            let history = History.versionsOf lookup (mirror (pathAt 0))
            let distinct = asked |> Seq.distinct |> Seq.length
            distinct = asked.Count && history |> List.map _.VersionId |> List.distinct |> List.length = history.Length)

    [<Fact>]
    let ``No version history remains for a previously tracked path`` () =
        Assert.Equal<string list>([ "No version history recorded for this path." ], Display.plainLines (HistoryView.ofHistory []))

    [<Fact>]
    let ``Change kind is visually distinguished`` () =
        let tones =
            [ ChangeKind.Added; ChangeKind.Changed; ChangeKind.Moved; ChangeKind.Deleted; ChangeKind.Linked ]
            |> List.map HistoryView.toneOfKind

        Assert.Equal(5, tones |> List.distinct |> List.length)

module ``Unknown path or version reported clearly`` =
    let private history =
        [ row 3L 3L "C\\a.txt" ChangeKind.Deleted 30 5L
          row 2L 2L "C\\a.txt" ChangeKind.Changed 20 5L
          row 1L 1L "C\\a.txt" ChangeKind.Added 10 5L ]

    [<Fact>]
    let ``Path never backed up`` () =
        let result = History.resolveVersion "C\\never.txt" (VersionSelector.ById 1L) []
        Assert.Equal("No history exists for path 'C\\never.txt'.", show result)

    [<Fact>]
    let ``a version id the path never had is reported`` () =
        let result = History.resolveVersion "C\\a.txt" (VersionSelector.ById 99L) history
        Assert.Equal("No version with id 99 was found for path 'C\\a.txt'.", show result)

    [<Fact>]
    let ``a deleted version cannot be named by its id`` () =
        let result = History.resolveVersion "C\\a.txt" (VersionSelector.ById 3L) history
        Assert.Equal("No version with id 3 was found for path 'C\\a.txt'.", show result)

    [<Fact>]
    let ``a date before the first version is reported`` () =
        match History.resolveVersion "C\\a.txt" (VersionSelector.AsOf(at 5)) history with
        | Error(HistoryError.NoVersionAt(path, moment)) ->
            Assert.Equal("C\\a.txt", path)
            Assert.Equal(at 5, moment)
        | other -> failwith $"unexpected {show other}"

    [<Fact>]
    let ``Restore without --recursive given a tracked directory path`` () =
        Assert.Equal(
            "'C\\docs' is a directory. Pass --recursive to restore it as a directory.",
            HistoryError.render (HistoryError.IsDirectory "C\\docs")
        )

module ``Restore of a deleted file`` =
    [<Fact>]
    let ``Restoring a deleted file`` () =
        let history =
            [ row 3L 3L "C\\a.txt" ChangeKind.Deleted 30 5L
              row 2L 2L "C\\a.txt" ChangeKind.Changed 20 7L
              row 1L 1L "C\\a.txt" ChangeKind.Added 10 5L ]

        let version = History.resolveVersion "C\\a.txt" (VersionSelector.AsOf(at 25)) history |> okOrFail
        let plan = Restore.planFile "C\\a.txt" (Destination.Explicit "D:\\out\\a.txt") version |> okOrFail
        Assert.Equal(2L, version.VersionId)
        Assert.Equal<string list>([ "D:\\out\\a.txt" ], plan.Writes |> List.map _.Destination)
        Assert.Equal(7L, plan.TotalBytes)

    [<Fact>]
    let ``a date after the deletion names nothing`` () =
        let history =
            [ row 3L 3L "C\\a.txt" ChangeKind.Deleted 30 5L
              row 1L 1L "C\\a.txt" ChangeKind.Added 10 5L ]

        Assert.Equal("No version of 'C\\a.txt' existed as of 2026-01-01T00:40:00.0000000+00:00.", show (History.resolveVersion "C\\a.txt" (VersionSelector.AsOf(at 40)) history))

module ``Interactive version selection when restoring`` =
    [<Fact>]
    let ``Selectable list excludes symlink/junction versions`` () =
        let history =
            [ row 4L 4L "C\\a.txt" ChangeKind.Deleted 40 5L
              linked 3L 3L "C\\a.txt" 30
              row 2L 2L "C\\a.txt" ChangeKind.Changed 20 5L
              row 1L 1L "C\\a.txt" ChangeKind.Added 10 5L ]

        Assert.Equal<int64 list>([ 2L; 1L ], History.restorableVersions history |> List.map _.VersionId)

module ``Restore a file at a given version or date`` =
    [<Fact>]
    let ``Restoring a previous version`` () =
        let history =
            [ row 2L 2L "C\\a.txt" ChangeKind.Changed 20 9L
              row 1L 1L "C\\a.txt" ChangeKind.Added 10 5L ]

        let version = History.resolveVersion "C\\a.txt" (VersionSelector.AsOf(at 15)) history |> okOrFail
        let plan = Restore.planFile "C\\a.txt" (Destination.Explicit "D:\\x") version |> okOrFail
        Assert.Equal(1L, version.VersionId)
        Assert.Equal(5L, plan.TotalBytes)
        Assert.Equal(at 10, plan.Writes.Head.ModifiedAt)

    [<Fact>]
    let ``Requested version is a symlink or junction entry`` () =
        let result = Restore.planFile "C\\l" (Destination.Explicit "D:\\x") (linked 1L 1L "C\\l" 10)

        Assert.Equal(
            "Cannot restore 'C\\l': the resolved version is a symlink/junction with no stored content.",
            show result
        )

module ``Restore to the original source location`` =
    [<Fact>]
    let ``Restoring in place to a location that no longer exists`` () =
        let version = row 1L 1L "C\\Users\\me\\a.txt" ChangeKind.Added 10 5L
        let plan = Restore.planFile "C\\Users\\me\\a.txt" Destination.InPlace version |> okOrFail
        Assert.Equal<string list>([ "C:\\Users\\me\\a.txt" ], plan.Writes |> List.map _.Destination)

    [<Fact>]
    let ``the original location maps the drive back`` () =
        Assert.Equal("C:\\x\\y.txt", SourceLocation.fromMirror "C\\x\\y.txt")
        Assert.Equal("C\\x\\y.txt", SourceLocation.toMirror "C:\\x\\y.txt")
        Assert.Equal("Users\\me", SourceLocation.fromMirror "Users\\me")

module ``Restore a directory at a given date`` =
    let private rows =
        [ row 1L 1L "C\\d\\kept.txt" ChangeKind.Added 10 5L
          row 2L 1L "C\\d\\gone.txt" ChangeKind.Added 10 7L
          row 3L 2L "C\\d\\gone.txt" ChangeKind.Deleted 20 7L
          row 4L 3L "C\\d\\later.txt" ChangeKind.Added 30 11L
          linked 5L 1L "C\\d\\sub\\link" 10
          row 6L 1L "C\\elsewhere.txt" ChangeKind.Added 10 100L ]

    let private plan (moment: Moment) = Restore.planDirectory "C\\d" moment (Destination.Explicit "D:\\out") rows |> okOrFail

    [<Fact>]
    let ``Restoring a directory to a new destination`` () =
        let plan = plan (Moment.AtTime(at 15))

        Assert.Equal<string list>(
            [ "D:\\out\\gone.txt"; "D:\\out\\kept.txt" ],
            plan.Writes |> List.map _.Destination
        )

    [<Fact>]
    let ``Restoring a directory brings back since-deleted files`` () =
        let plan = plan (Moment.AtTime(at 15))
        Assert.Contains("D:\\out\\gone.txt", plan.Writes |> List.map _.Destination)

    [<Fact>]
    let ``Restoring a directory excludes files added afterward`` () =
        let plan = plan (Moment.AtTime(at 15))
        Assert.DoesNotContain("D:\\out\\later.txt", plan.Writes |> List.map _.Destination)

    [<Fact>]
    let ``Restoring a directory removes tracked content that did not exist at that date`` () =
        let plan = plan (Moment.AtTime(at 15))
        Assert.Equal<string list>([ "D:\\out\\later.txt" ], plan.Removals)

    [<Fact>]
    let ``Restoring a directory in place`` () =
        let plan = Restore.planDirectory "C\\d" (Moment.AtTime(at 15)) Destination.InPlace rows |> okOrFail

        Assert.Equal<string list>(
            [ "C:\\d\\gone.txt"; "C:\\d\\kept.txt" ],
            plan.Writes |> List.map _.Destination
        )

    [<Fact>]
    let ``Recursive restore of a directory containing a symlink/junction`` () =
        let plan = plan (Moment.AtTime(at 15))
        Assert.Equal<string list>([ "C\\d\\sub\\link" ], plan.Skipped |> List.map MirrorPath.value)
        Assert.DoesNotContain("link", String.Join(";", plan.Removals))

    [<Fact>]
    let ``Recursive restore of a directory never tracked`` () =
        let result = Restore.planDirectory "C\\nothing" Moment.Latest (Destination.Explicit "D:\\out") rows

        Assert.Equal("No such directory is recorded in the backup: 'C\\nothing'.", show result)

    [<Fact>]
    let ``Recursive restore without an explicit date`` () =
        let plan = plan Moment.Latest

        Assert.Equal<string list>(
            [ "D:\\out\\kept.txt"; "D:\\out\\later.txt" ],
            plan.Writes |> List.map _.Destination
        )

        Assert.Empty plan.Removals

    [<Fact>]
    let ``a snapshot id names the state after that snapshot`` () =
        let plan = plan (Moment.AtSnapshot 1L)
        Assert.Equal<string list>([ "D:\\out\\gone.txt"; "D:\\out\\kept.txt" ], plan.Writes |> List.map _.Destination)

    [<Fact>]
    let ``removals only count the files that exist`` () =
        let plan = plan (Moment.AtTime(at 15)) |> Restore.keepExistingRemovals (fun _ -> false)
        Assert.Empty plan.Removals

    /// Random histories of a few paths: whatever happened, removals add nothing to the bytes or the files.
    [<Property>]
    let ``removals never contribute to the plan's bytes or files`` () =
        let gen =
            gen {
                let! count = Gen.choose (1, 12)

                let! steps =
                    Gen.listOfLength
                        count
                        (Gen.zip3 (Gen.choose (0, 3)) (Gen.elements [ ChangeKind.Added; ChangeKind.Changed; ChangeKind.Deleted ]) (Gen.choose (1, 50)))

                let! cutoff = Gen.choose (0, 12)
                return steps, cutoff
            }

        Prop.forAll (Arb.fromGen gen) (fun (steps, cutoff) ->
            let versions =
                steps
                |> List.mapi (fun index (path, kind, size) -> row (int64 (index + 1)) (int64 (index + 1)) $"C\\d\\f{path}.txt" kind (index + 1) (int64 size))

            match Restore.planDirectory "C\\d" (Moment.AtSnapshot(int64 cutoff)) (Destination.Explicit "D:\\o") versions with
            | Error _ -> true
            | Ok plan ->
                let live = History.stateAt (Moment.AtSnapshot(int64 cutoff)) versions
                let liveSizes = live |> Array.sumBy (fun version -> match version.Row.Content with RowContent.Regular c -> c.Size | RowContent.Linked _ -> 0L)
                let liveNames = live |> Array.map (fun version -> MirrorPath.value version.Row.RowPath) |> Set.ofArray

                plan.TotalBytes = (plan.Writes |> List.sumBy _.Size)
                && plan.TotalBytes = liveSizes
                && plan.Writes |> List.forall (fun write -> Set.contains (MirrorPath.value write.Path) liveNames))

module ``Restore never writes into the live mirror`` =
    let private root = "C:\\Target"

    let private refuses (destination: string) : bool =
        match Restore.guard root destination with
        | Error(HistoryError.DestinationInMirror _) -> true
        | Error _
        | Ok() -> false

    [<Fact>]
    let ``Destination resolves inside the profile's live mirror`` () =
        Assert.True(refuses "C:\\Target\\C\\a.txt")
        Assert.True(refuses "C:\\Target")
        Assert.False(refuses "C:\\Elsewhere\\a.txt")
        Assert.False(refuses "C:\\TargetAndMore\\a.txt")

    [<Fact>]
    let ``A recursive restore's computed destination resolves inside the live mirror`` () =
        let plan =
            Restore.planDirectory
                "C\\d"
                Moment.Latest
                (Destination.Explicit "C:\\Target\\out")
                [ row 1L 1L "C\\d\\a.txt" ChangeKind.Added 10 5L ]
            |> okOrFail

        Assert.True(Restore.destinations plan |> List.exists refuses)

    [<Fact>]
    let ``Destination reached only through a reparse point into the mirror`` () =
        // The shell passes the real path after following the link, so the guard sees the mirror path.
        Assert.True(refuses "c:\\target\\C\\deep\\a.txt")

    [<Property>]
    let ``the guard rejects every spelling of a path under the mirror root`` () =
        let spellings =
            gen {
                let! segments = Gen.listOfLength 2 (Gen.elements [ "a"; "B"; "c.txt"; "Dd" ])
                let! upper = Gen.elements [ true; false ]
                let! trailing = Gen.elements [ ""; "\\"; "/" ]
                let! detour = Gen.elements [ ""; "x\\..\\"; "..\\Target\\" ]
                let cased = if upper then root.ToUpperInvariant() else root.ToLowerInvariant()
                return cased + "\\" + detour + String.Join("\\", segments) + trailing
            }

        Prop.forAll (Arb.fromGen spellings) refuses

module ``Flexible path input for history, restore, show, and diff`` =
    let private context (cwd: string) : PathContext =
        { MirrorRoot = "T:\\Target"
          WorkingDirectory = cwd
          IsWithinMirror = _.StartsWith("T:\\Target", StringComparison.OrdinalIgnoreCase) }

    let private resolve (cwd: string) (history: string list) (raw: string) : string option =
        PathInput.resolve (context cwd) (fun candidate -> List.contains candidate history) raw |> ValueOption.toOption

    [<Fact>]
    let ``Path given in its mirror-relative form`` () =
        Assert.Equal(Some "C\\docs\\a.txt", resolve "C:\\Work" [ "C\\docs\\a.txt" ] "C\\docs\\a.txt")

    [<Fact>]
    let ``Path relative to a working directory inside the mirror`` () =
        // The same text is also a recorded path elsewhere; the working-directory reading wins.
        let history = [ "C\\docs\\a.txt"; "a.txt" ]
        Assert.Equal(Some "C\\docs\\a.txt", resolve "T:\\Target\\C\\docs" history "a.txt")

    [<Fact>]
    let ``Path relative to a working directory inside the mirror, no cwd-relative match`` () =
        Assert.Equal(Some "a.txt", resolve "T:\\Target\\C\\docs" [ "a.txt" ] "a.txt")

    [<Fact>]
    let ``Path relative to a working directory inside a source`` () =
        Assert.Equal(Some "C\\Users\\me\\a.txt", resolve "C:\\Users\\me" [ "C\\Users\\me\\a.txt" ] "a.txt")

    [<Fact>]
    let ``Absolute source path given directly`` () =
        Assert.Equal(Some "C\\Users\\me\\a.txt", resolve "D:\\Other" [ "C\\Users\\me\\a.txt" ] "C:\\Users\\me\\a.txt")

    [<Fact>]
    let ``No interpretation matches recorded history`` () =
        Assert.Equal(None, resolve "C:\\Work" [ "C\\other.txt" ] "missing.txt")

    let private directory (cwd: string) (tracked: string list) (raw: string) : DirectoryResolution =
        PathInput.resolveDirectory (context cwd) (fun candidate -> List.contains candidate tracked) raw

    [<Fact>]
    let ``Browsing or recursively restoring the current directory from within a source`` () =
        let resolution = directory "C:\\Users\\me" [ "C\\Users\\me" ] "."
        Assert.Equal("C\\Users\\me", resolution.Directory)
        Assert.False resolution.FellBackToMirrorRoot

    [<Fact>]
    let ``Current directory outside the mirror has no recorded history`` () =
        let resolution = directory "C:\\Users\\me" [ "C\\other" ] "."
        Assert.Equal(".", resolution.Directory)
        Assert.True resolution.FellBackToMirrorRoot

    [<Fact>]
    let ``Non-root directory argument from within a source is unaffected`` () =
        let resolution = directory "C:\\Users\\me" [ "C\\docs" ] "C\\docs"
        Assert.Equal("C\\docs", resolution.Directory)
        Assert.False resolution.FellBackToMirrorRoot

module ``One-level directory listing`` =
    let private rows =
        [ row 1L 1L "C\\d\\a.txt" ChangeKind.Added 10 5L
          row 2L 1L "C\\d\\sub\\deep.txt" ChangeKind.Added 10 7L
          row 3L 1L "C\\top.txt" ChangeKind.Added 10 9L ]

    [<Fact>]
    let ``Listing a directory's current contents`` () =
        let entries = History.listing "C\\d" Moment.Latest false rows |> okOrFail
        Assert.Equal<string list>([ "a.txt"; "sub" ], names entries)
        Assert.Equal(EntryKind.File, entries[0].Kind)
        Assert.Equal(EntryKind.Directory, entries[1].Kind)
        Assert.Equal(ValueSome 5L, entries[0].Size)

    [<Fact>]
    let ``Directory never tracked`` () =
        Assert.Equal("No such directory is recorded in the backup: 'C\\nope'.", show (History.listing "C\\nope" Moment.Latest false rows))

    [<Fact>]
    let ``Browsing a profile that has never completed a backup run`` () =
        Assert.Equal("No such directory is recorded in the backup: 'C'.", show (History.listing "C" Moment.Latest false []))

module ``Point-in-time directory listing`` =
    [<Fact>]
    let ``Listing a directory as it looked on a past date`` () =
        let rows =
            [ row 1L 1L "C\\d\\old.txt" ChangeKind.Added 10 5L
              row 2L 2L "C\\d\\old.txt" ChangeKind.Deleted 20 5L
              row 3L 3L "C\\d\\new.txt" ChangeKind.Added 30 5L
              row 4L 1L "C\\d\\kept.txt" ChangeKind.Added 10 5L ]

        let before = History.listing "C\\d" (Moment.AtTime(at 15)) false rows |> okOrFail
        Assert.Equal<string list>([ "kept.txt"; "old.txt" ], names before)
        let after = History.listing "C\\d" (Moment.AtTime(at 25)) false rows |> okOrFail
        Assert.Equal<string list>([ "kept.txt" ], names after)

module ``Including deleted entries in a directory listing`` =
    let private rows =
        [ row 1L 1L "C\\d\\live.txt" ChangeKind.Added 10 5L
          row 2L 1L "C\\d\\gone.txt" ChangeKind.Added 10 5L
          row 3L 2L "C\\d\\gone.txt" ChangeKind.Deleted 20 5L
          row 4L 1L "C\\d\\left.txt" ChangeKind.Added 10 5L
          row 5L 2L "C\\d\\left.txt" ChangeKind.Deleted 20 5L
          moved 6L 2L "C\\elsewhere\\left.txt" "C\\d\\left.txt" 20
          row 7L 1L "C\\d\\emptied\\x.txt" ChangeKind.Added 10 5L
          row 8L 2L "C\\d\\emptied\\x.txt" ChangeKind.Deleted 20 5L ]

    [<Fact>]
    let ``Including deleted entries in the current listing`` () =
        let entries = History.listing "C\\d" Moment.Latest true rows |> okOrFail
        // Interleaved by name, not grouped.
        Assert.Equal<string list>([ "emptied"; "gone.txt"; "left.txt"; "live.txt" ], names entries)
        Assert.Equal(EntryStatus.Deleted, (entries |> List.find (fun entry -> entry.Name = "gone.txt")).Status)
        Assert.Equal(EntryStatus.Live, (entries |> List.find (fun entry -> entry.Name = "live.txt")).Status)

    [<Fact>]
    let ``Including deleted entries in a point-in-time listing`` () =
        let entries = History.listing "C\\d" (Moment.AtTime(at 25)) true rows |> okOrFail
        Assert.Equal<string list>([ "emptied"; "gone.txt"; "left.txt"; "live.txt" ], names entries)
        let before = History.listing "C\\d" (Moment.AtTime(at 15)) true rows |> okOrFail
        Assert.Equal(EntryStatus.Live, (before |> List.find (fun entry -> entry.Name = "gone.txt")).Status)

    [<Fact>]
    let ``Entry moved out of the listed directory`` () =
        let entries = History.listing "C\\d" Moment.Latest true rows |> okOrFail

        Assert.Equal(EntryStatus.Moved "C\\elsewhere\\left.txt", (entries |> List.find (fun entry -> entry.Name = "left.txt")).Status)

    [<Fact>]
    let ``Subdirectory whose contents are entirely deleted`` () =
        let entries = History.listing "C\\d" Moment.Latest true rows |> okOrFail
        let emptied = entries |> List.find (fun entry -> entry.Name = "emptied")
        Assert.Equal(EntryKind.Directory, emptied.Kind)
        Assert.Equal(EntryStatus.Deleted, emptied.Status)

    [<Fact>]
    let ``without the option, deleted entries are not listed`` () =
        let entries = History.listing "C\\d" Moment.Latest false rows |> okOrFail
        Assert.Equal<string list>([ "live.txt" ], names entries)

module ``Recently deleted report`` =
    let private rows =
        [ row 1L 1L "C\\a\\one.txt" ChangeKind.Added 10 5L
          row 2L 2L "C\\a\\one.txt" ChangeKind.Deleted 20 5L
          row 3L 1L "C\\b\\two.txt" ChangeKind.Added 10 7L
          row 4L 3L "C\\b\\two.txt" ChangeKind.Deleted 30 7L
          row 5L 1L "C\\a\\live.txt" ChangeKind.Added 10 1L ]

    [<Fact>]
    let ``Listing recently deleted files across a profile`` () =
        let deleted = History.recentlyDeleted "" ValueNone rows
        Assert.Equal<string list>([ "C\\b\\two.txt"; "C\\a\\one.txt" ], deleted |> List.map (fun entry -> MirrorPath.value entry.DeletedPath))

    [<Fact>]
    let ``Scoping the recently deleted report to a subtree`` () =
        let deleted = History.recentlyDeleted "C\\a" ValueNone rows
        Assert.Equal<string list>([ "C\\a\\one.txt" ], deleted |> List.map (fun entry -> MirrorPath.value entry.DeletedPath))

    [<Fact>]
    let ``No deleted files found`` () =
        Assert.Empty(History.recentlyDeleted "C\\a\\live" ValueNone rows)

    [<Fact>]
    let ``a file deleted and added again is not reported`` () =
        let again = rows @ [ row 6L 4L "C\\a\\one.txt" ChangeKind.Added 40 5L ]
        Assert.Equal<string list>([ "C\\b\\two.txt" ], History.recentlyDeleted "" ValueNone again |> List.map (fun entry -> MirrorPath.value entry.DeletedPath))

module ``Point in time state`` =
    /// For any rows and any time, the state at that time holds no row recorded after it.
    [<Property>]
    let ``the version at a moment never lies after it`` () =
        let gen =
            gen {
                let! count = Gen.choose (0, 15)
                let! steps = Gen.listOfLength count (Gen.zip (Gen.choose (0, 4)) (Gen.elements [ ChangeKind.Added; ChangeKind.Changed; ChangeKind.Deleted ]))
                let! cutoff = Gen.choose (0, 20)
                return steps, cutoff
            }

        Prop.forAll (Arb.fromGen gen) (fun (steps, cutoff) ->
            let versions =
                steps |> List.mapi (fun index (path, kind) -> row (int64 (index + 1)) (int64 (index + 1)) $"C\\p{path}" kind (index + 1) 1L)

            let state = History.stateAt (Moment.AtTime(at cutoff)) versions
            state |> Array.forall (fun version -> version.Row.RecordedAt <= at cutoff && version.Row.Kind <> ChangeKind.Deleted))

module ``Snapshot change rollups use change-kind colors`` =
    let private rollup =
        History.rollup
            ""
            false
            [ row 1L 1L "C\\a\\one.txt" ChangeKind.Added 10 1L
              row 2L 1L "C\\a\\b\\two.txt" ChangeKind.Changed 10 1L
              row 3L 1L "C\\a\\b\\three.txt" ChangeKind.Deleted 10 1L
              linked 4L 1L "C\\a\\l" 10 ]

    [<Fact>]
    let ``Distinguishing change kinds in directory rollups`` () =
        let display = HistoryView.ofRollup (snapshot 1L 10 SnapshotStatus.Complete) "" Set.empty ValueNone rollup

        let line = display |> List.pick (function Line.Spans spans when spans |> List.exists (fun span -> span.Text = "a") -> Some spans | _ -> None)
        let tones = line |> List.map _.Tone |> List.distinct
        // The directory name is plain; each count is in its own kind's tone.
        Assert.True(tones |> List.contains Tone.Plain)
        Assert.True(tones.Length >= 5)

    [<Fact>]
    let ``Reading rollups without color support`` () =
        let display = HistoryView.ofRollup (snapshot 1L 10 SnapshotStatus.Complete) "" Set.empty ValueNone rollup
        let text = plain display
        Assert.Contains("+1", text)
        Assert.Contains("~1", text)
        Assert.Contains("-1", text)
        Assert.Contains("link 1", text)

module ``Inspect changes recorded by a snapshot`` =
    let private changes =
        [ row 1L 1L "C\\a\\one.txt" ChangeKind.Added 10 1L
          row 2L 1L "C\\a\\b\\two.txt" ChangeKind.Changed 10 1L
          row 3L 1L "C\\a\\b\\c\\three.txt" ChangeKind.Deleted 10 1L
          moved 4L 1L "C\\a\\moved.txt" "C\\z\\old.txt" 10 ]

    let private view (scope: string) (files: bool) (depth: int voption) (sources: string list) =
        HistoryView.ofRollup (snapshot 1L 10 SnapshotStatus.Complete) scope (Set.ofList sources) depth (History.rollup scope files changes)
        |> plain

    [<Fact>]
    let ``Showing a snapshot's directory rollups`` () =
        let text = view "" false ValueNone []
        Assert.Contains("Snapshot #1", text)
        Assert.Contains("a (+1 ~1 ->1 -1 link 0)", text)
        Assert.Contains("c (+0 ~0 ->0 -1 link 0)", text)

    [<Fact>]
    let ``Identifying configured source roots`` () =
        Assert.Contains("a [source]", view "" false ValueNone [ "C\\a" ])

    [<Fact>]
    let ``Scoping the report to a directory`` () =
        let text = view "C\\a\\b" false ValueNone []
        Assert.DoesNotContain("moved.txt", text)
        Assert.Contains("+0 ~1 ->0 -1", text)

    [<Fact>]
    let ``Showing changed file paths on request`` () =
        let text = view "" true ValueNone []
        Assert.Contains("one.txt - Added", text)
        Assert.Contains("moved.txt - Moved from C\\z\\old.txt", text)

    [<Fact>]
    let ``Omitting file paths by default`` () =
        Assert.DoesNotContain("one.txt", view "" false ValueNone [])

    [<Fact>]
    let ``Limiting displayed directory depth`` () =
        let text = view "C" false (ValueSome 1) []
        Assert.Contains("a (", text)
        Assert.DoesNotContain("b (", text)

    [<Fact>]
    let ``Showing only the selected scope root`` () =
        let text = view "C" false (ValueSome 0) []
        Assert.DoesNotContain("a (", text)

    [<Fact>]
    let ``Indicating omitted affected directories`` () =
        let text = view "C" false (ValueSome 1) []
        Assert.Contains("... 2 deeper directories not shown", text)

    [<Fact>]
    let ``Preserving rollups across a depth limit`` () =
        let text = view "C" false (ValueSome 0) []
        Assert.Contains("(+1 ~1 ->1 -1 link 0)", text)

    [<Fact>]
    let ``Preserving unlimited depth by default`` () =
        let text = view "C" false ValueNone []
        Assert.Contains("c (", text)

    [<Fact>]
    let ``Combining depth and file details`` () =
        let text = view "C" true (ValueSome 1) []
        Assert.Contains("one.txt - Added", text)
        Assert.DoesNotContain("two.txt", text)
        Assert.DoesNotContain("three.txt", text)

    [<Fact>]
    let ``No changes in the requested scope`` () =
        Assert.Empty(History.rollup "C\\nothing" false changes)

module ``Interactive and explicit snapshot selection for a directory restore`` =
    let private snapshots =
        [ snapshot 1L 10 SnapshotStatus.Complete
          snapshot 2L 20 SnapshotStatus.Cancelled
          snapshot 3L 30 SnapshotStatus.Failed
          snapshot 4L 40 SnapshotStatus.Running
          snapshot 5L 50 SnapshotStatus.Complete ]

    let private rows =
        [ row 1L 1L "C\\d\\a.txt" ChangeKind.Added 10 5L
          row 2L 1L "C\\d\\b.txt" ChangeKind.Added 10 7L
          linked 3L 1L "C\\d\\l" 10
          row 4L 2L "C\\d\\a.txt" ChangeKind.Changed 20 9L
          row 5L 3L "C\\d\\c.txt" ChangeKind.Added 30 1L
          row 6L 4L "C\\d\\c.txt" ChangeKind.Changed 40 1L
          row 7L 2L "C\\other\\x.txt" ChangeKind.Added 20 1L ]

    [<Fact>]
    let ``Only snapshots that touched the directory are offered`` () =
        let candidates = History.directoryCandidates "C\\d" snapshots rows
        Assert.DoesNotContain(5L, candidates |> List.map _.Snapshot.SnapshotId)

    [<Fact>]
    let ``Running and failed snapshots are never offered`` () =
        let candidates = History.directoryCandidates "C\\d" snapshots rows
        Assert.Equal<int64 list>([ 2L; 1L ], candidates |> List.map _.Snapshot.SnapshotId)

    [<Fact>]
    let ``Picker entries report directory-scoped statistics`` () =
        let first = History.directoryCandidates "C\\d" snapshots rows |> List.find (fun candidate -> candidate.Snapshot.SnapshotId = 1L)
        Assert.Equal(2, first.FilesAdded)
        Assert.Equal(0, first.FilesChanged)
        Assert.Equal(12L, first.NetBytes)
        Assert.Equal(2, first.TotalFiles)
        Assert.Equal(12L, first.TotalBytes)
        Assert.Equal(1, first.TotalLinks)
        let second = History.directoryCandidates "C\\d" snapshots rows |> List.find (fun candidate -> candidate.Snapshot.SnapshotId = 2L)
        Assert.Equal(1, second.FilesChanged)
        // The running snapshot's later rows do not count towards a candidate before it.
        Assert.Equal(2, second.TotalFiles)
        Assert.Equal(16L, second.TotalBytes)

    [<Fact>]
    let ``Cancelled snapshots are visually distinguished`` () =
        let tones =
            [ SnapshotStatus.Complete; SnapshotStatus.Cancelled ] |> List.map HistoryView.toneOfStatus

        Assert.Equal(2, tones |> List.distinct |> List.length)

module ``Invalid datetime option input produces a friendly error`` =
    let private parse (text: string) = DateInput.parse (fun _ -> TimeSpan.FromHours 2.0) "--at" text

    [<Fact>]
    let ``Unparseable value given to a date/time option`` () =
        match parse "yesterday" with
        | Error error ->
            Assert.Equal("--at", error.Option)
            Assert.Equal("yesterday", error.Value)
            Assert.Contains("'--at'", DateInput.describe error)
            Assert.Contains("'yesterday'", DateInput.describe error)
        | Ok _ -> failwith "parsed"

    [<Fact>]
    let ``Ambiguous slash-separated date given to a date/time option`` () =
        Assert.True((parse "01/02/2025").IsError)

    [<Fact>]
    let ``Valid value still parses as before`` () =
        Assert.Equal(Ok(DateTimeOffset(2025, 1, 15, 14, 30, 0, TimeSpan.FromHours 2.0)), parse "2025-01-15 14:30")
        Assert.Equal(Ok(DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.FromHours 2.0)), parse "2025-01-15")
        Assert.Equal(Ok(DateTimeOffset(2025, 1, 15, 14, 30, 0, TimeSpan.FromHours 1.0)), parse "2025-01-15T14:30:00+01:00")

module ``Datetime options document an example value in their help text`` =
    [<Fact>]
    let ``Viewing help for a command with a date/time option`` () =
        let help = DateInput.help "List the directory as of this date/time"
        Assert.Contains(DateInput.example, help)

    /// Every value the help text quotes is accepted, in whatever zone the machine is.
    [<Property>]
    let ``the help example always parses`` () =
        let examples = [ for found in Regex.Matches(DateInput.example, "'([^']+)'") -> found.Groups[1].Value ]
        Assert.NotEmpty examples

        Prop.forAll (Arb.fromGen (Gen.choose (-12, 14))) (fun hours ->
            examples |> List.forall (fun text -> (DateInput.parse (fun _ -> TimeSpan.FromHours(float hours)) "--at" text).IsOk))
