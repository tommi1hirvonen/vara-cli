/// Tests of retention evaluation and the prune plan, on plain data. Each module is named after a requirement of
/// openspec/specs/retention-pruning/spec.md and each test after one of its scenarios; the properties are named after
/// the rule they check.
module Vara.Domain.Tests.RetentionTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain
open Vara.Domain.Tests.Fixtures

let private noStats: Counts =
    { FilesAdded = 0
      FilesChanged = 0
      FilesMoved = 0
      FilesDeleted = 0
      FilesFailed = 0
      BytesTransferred = 0L }

let private snapshotAt (id: int64) (status: SnapshotStatus) (completed: DateTimeOffset) : SnapshotInfo =
    { SnapshotId = id
      StartedAt = completed.AddMinutes -1.0
      CompletedAt = ValueSome completed
      Status = status
      Stats = noStats }

let private complete (id: int64) (completed: DateTimeOffset) : SnapshotInfo = snapshotAt id SnapshotStatus.Complete completed

let private utc (year: int) (month: int) (day: int) (hour: int) : DateTimeOffset =
    DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero)

let private count (n: int) : RetentionCount = (RetentionCount.tryCreate n).Value

let private policy (daily: int) (weekly: int) (monthly: int) (yearly: int) : RetentionPolicy =
    { KeepDaily = count daily
      KeepWeekly = count weekly
      KeepMonthly = count monthly
      KeepYearly = count yearly }

let private eligibleOf (policy: RetentionPolicy) (snapshots: SnapshotInfo list) : int64 list =
    (Retention.evaluate policy snapshots).Eligible

module ``Tiered retention evaluation`` =
    [<Fact>]
    let ``Snapshot within a retained daily bucket`` () =
        // Two snapshots on 2026-03-01: only the later one is kept for that day. The 2nd is the newest overall.
        let snapshots =
            [ complete 1L (utc 2026 3 1 10)
              complete 2L (utc 2026 3 1 18)
              complete 3L (utc 2026 3 2 12) ]

        let decision = Retention.evaluate (policy 2 0 0 0) snapshots
        Assert.Equal<Set<int64>>(set [ 2L; 3L ], decision.Kept)
        Assert.Equal<int64 list>([ 1L ], decision.Eligible)

    [<Fact>]
    let ``All tiers configured to retain nothing`` () =
        let snapshots =
            [ complete 1L (utc 2026 3 1 10)
              complete 2L (utc 2026 3 2 10)
              complete 3L (utc 2026 3 3 10) ]

        let decision = Retention.evaluate (policy 0 0 0 0) snapshots
        // The newest completed snapshot stays; every other completed snapshot may go.
        Assert.Equal<Set<int64>>(set [ 3L ], decision.Kept)
        Assert.Equal<int64 list>([ 1L; 2L ], decision.Eligible)

    [<Fact>]
    let ``Most recent completed snapshot recorded no file changes`` () =
        // `noStats` records zero added, changed, moved and deleted files for every snapshot, the newest included.
        let newest = complete 2L (utc 2026 3 2 10)
        Assert.Equal(0, newest.Stats.FilesAdded + newest.Stats.FilesChanged + newest.Stats.FilesMoved + newest.Stats.FilesDeleted)
        let decision = Retention.evaluate (policy 0 0 0 0) [ complete 1L (utc 2026 3 1 10); newest ]
        Assert.True(decision.Kept.Contains 2L)
        Assert.DoesNotContain(2L, decision.Eligible)

    [<Fact>]
    let ``Snapshot timestamp near a calendar boundary in a non-zero offset`` () =
        // 2026-01-01T00:30:00+03:00 is 2025-12-31T21:30Z: its UTC date is in the previous day, month and year.
        let boundary = DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.FromHours 3.0)
        let snapshot = complete 1L boundary
        let date = Retention.utcDateOf snapshot
        Assert.Equal(DateOnly(2025, 12, 31), Retention.Bucket.day date)
        Assert.Equal(DateOnly(2025, 12, 29), Retention.Bucket.week date) // the Monday of that ISO week
        Assert.Equal(DateOnly(2025, 12, 1), Retention.Bucket.month date)
        Assert.Equal(DateOnly(2025, 1, 1), Retention.Bucket.year date)

        // Every tier, with room for two buckets, keeps it as the second bucket beside a newer snapshot in 2026: so
        // none of them put it in 2026 with that snapshot.
        let newer = complete 2L (utc 2026 3 18 12)

        for tierPolicy in [ policy 2 0 0 0; policy 0 2 0 0; policy 0 0 2 0; policy 0 0 0 2 ] do
            Assert.Empty(eligibleOf tierPolicy [ snapshot; newer ])

    [<Fact>]
    let ``a snapshot at the boundary shares its tier bucket with the day before, not the day after`` () =
        // At 2025-12-31T10:00Z and the boundary instant (2025-12-31T21:30Z): one UTC day, so one daily bucket.
        let earlier = complete 1L (utc 2025 12 31 10)
        let boundary = complete 2L (DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.FromHours 3.0))
        let newest = complete 3L (utc 2026 1 5 12)
        Assert.Equal<int64 list>([ 1L ], eligibleOf (policy 2 0 0 0) [ earlier; boundary; newest ])

    [<Fact>]
    let ``a snapshot with no completion time is placed at its start time`` () =
        let started = { complete 1L (utc 2026 3 1 10) with CompletedAt = ValueNone; StartedAt = utc 2026 3 1 10 }
        Assert.Equal(DateOnly(2026, 3, 1), Retention.utcDateOf started)

    [<Fact>]
    let ``weeks start on Monday`` () =
        // 2026-03-01 is a Sunday: its week began on Monday 2026-02-23; 2026-03-02 is a Monday and begins its own.
        Assert.Equal(DateOnly(2026, 2, 23), Retention.Bucket.week (DateOnly(2026, 3, 1)))
        Assert.Equal(DateOnly(2026, 3, 2), Retention.Bucket.week (DateOnly(2026, 3, 2)))

    [<Fact>]
    let ``a failed or cancelled snapshot is eligible when not kept, and a running one never is`` () =
        let snapshots =
            [ snapshotAt 1L SnapshotStatus.Failed (utc 2026 3 1 10)
              snapshotAt 2L SnapshotStatus.Cancelled (utc 2026 3 2 10)
              snapshotAt 3L SnapshotStatus.Running (utc 2026 3 3 10)
              complete 4L (utc 2026 3 4 10) ]

        Assert.Equal<int64 list>([ 1L; 2L ], eligibleOf (policy 5 5 5 5) snapshots)

    [<Fact>]
    let ``there is nothing to keep or remove without snapshots`` () =
        let decision = Retention.evaluate (policy 3 3 3 3) []
        Assert.Empty decision.Kept
        Assert.Empty decision.Eligible

// ---- properties -------------------------------------------------------------------------------------------------------

let private statuses =
    [ SnapshotStatus.Complete
      SnapshotStatus.Complete
      SnapshotStatus.Complete
      SnapshotStatus.Failed
      SnapshotStatus.Cancelled
      SnapshotStatus.Running ]

/// A snapshot at a random time of a few years, in a random offset. Times are coarse, so that several snapshots share
/// a day, a week or a month.
let private snapshotGen (id: int64) : Gen<SnapshotInfo> =
    gen {
        let! day = Gen.choose (0, 800)
        let! hour = Gen.choose (0, 23)
        let! offsetHours = Gen.choose (-12, 14)
        let! status = Gen.elements statuses
        let instant = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(float day).AddHours(float hour)
        return snapshotAt id status (instant.ToOffset(TimeSpan.FromHours(float offsetHours)))
    }

let private snapshotsGen: Gen<SnapshotInfo list> =
    gen {
        let! n = Gen.choose (0, 25)
        return! [ 1..n ] |> List.map (fun id -> snapshotGen (int64 id)) |> Gen.sequenceToList
    }

let private policyGen: Gen<RetentionPolicy> =
    gen {
        let! daily = Gen.choose (0, 6)
        let! weekly = Gen.choose (0, 6)
        let! monthly = Gen.choose (0, 6)
        let! yearly = Gen.choose (0, 3)
        return policy daily weekly monthly yearly
    }

let private arb (gen: Gen<'a>) : Arbitrary<'a> = Arb.fromGen gen

let private casesGen = Gen.zip snapshotsGen policyGen
let private cases = arb casesGen

/// Re-expresses each snapshot's times in another offset: the same instants.
let private withOffset (offset: TimeSpan) (snapshot: SnapshotInfo) : SnapshotInfo =
    { snapshot with
        StartedAt = snapshot.StartedAt.ToOffset offset
        CompletedAt = snapshot.CompletedAt |> ValueOption.map (fun time -> time.ToOffset offset) }

[<Property>]
let ``a snapshot kept by any tier is never in the eligible set`` () =
    Prop.forAll cases (fun (snapshots, policy) ->
        let decision = Retention.evaluate policy snapshots
        decision.Eligible |> List.forall (fun id -> not (decision.Kept.Contains id)))

[<Property>]
let ``the newest completed snapshot is always kept`` () =
    Prop.forAll cases (fun (snapshots, policy) ->
        let newest =
            snapshots
            |> List.filter (fun snapshot -> snapshot.Status = SnapshotStatus.Complete)
            |> List.sortBy (fun snapshot -> struct (-snapshot.CompletedAt.Value.UtcTicks, -snapshot.SnapshotId))
            |> List.tryHead

        match newest with
        | None -> true
        | Some snapshot ->
            let decision = Retention.evaluate policy snapshots
            decision.Kept.Contains snapshot.SnapshotId && not (List.contains snapshot.SnapshotId decision.Eligible))

[<Property>]
let ``the decision does not depend on input order`` () =
    Prop.forAll cases (fun (snapshots, policy) ->
        let expected = Retention.evaluate policy snapshots

        Retention.evaluate policy (List.rev snapshots) = expected
        && Retention.evaluate policy (snapshots |> List.sortBy (fun snapshot -> snapshot.StartedAt.Hour, snapshot.SnapshotId)) = expected)

[<Property>]
let ``a running snapshot is never eligible`` () =
    Prop.forAll cases (fun (snapshots, policy) ->
        let eligible = Set.ofList (Retention.evaluate policy snapshots).Eligible

        snapshots
        |> List.forall (fun snapshot -> snapshot.Status <> SnapshotStatus.Running || not (eligible.Contains snapshot.SnapshotId)))

[<Property>]
let ``every tier buckets by the UTC date whatever offset is recorded`` () =
    let offsets = [ -12 .. 14 ] |> List.map (fun hours -> TimeSpan.FromHours(float hours))

    Prop.forAll (Gen.zip casesGen (Gen.elements offsets) |> arb) (fun ((snapshots, policy), offset) ->
        Retention.evaluate policy snapshots = Retention.evaluate policy (snapshots |> List.map (withOffset offset)))

module ``Prune command`` =
    // The plan: which snapshots a run removes. The command's own scenarios are tested through the command.
    [<Fact>]
    let ``A snapshot becomes eligible after the prompt was shown but before removal runs`` () =
        // The user was shown 1 and 2; by the time the removal runs, 3 is eligible as well, and it is left alone.
        let removed = Prune.snapshotsToRemove [ 1L; 2L; 3L ] (ValueSome [ 1L; 2L ]) (set [ 1L; 2L; 3L ])
        Assert.Equal<int64 list>([ 1L; 2L ], removed)

    [<Fact>]
    let ``A confirmed snapshot no longer exists by the time removal runs`` () =
        // The user confirmed 1, 2 and 3; 3 is gone, so it is not eligible now. The others are still removed.
        let removed = Prune.snapshotsToRemove [ 1L; 2L ] (ValueSome [ 1L; 2L; 3L ]) (set [ 1L; 2L ])
        Assert.Equal<int64 list>([ 1L; 2L ], removed)

    [<Fact>]
    let ``without a prompt, every eligible snapshot that exists is removed`` () =
        let removed = Prune.snapshotsToRemove [ 5L; 2L ] ValueNone (set [ 2L; 5L; 9L ])
        Assert.Equal<int64 list>([ 2L; 5L ], removed)

    [<Property>]
    let ``the removed set is a subset of both the eligible and the confirmed sets`` () =
        let ids = Gen.listOf (Gen.choose (1, 12) |> Gen.map int64)

        Prop.forAll (Gen.zip3 ids ids ids |> arb) (fun (eligible, confirmed, existing) ->
            let removed = Prune.snapshotsToRemove eligible (ValueSome confirmed) (Set.ofList existing)

            removed |> List.forall (fun id -> List.contains id eligible && List.contains id confirmed && List.contains id existing))

module ``Unreferenced content garbage collection`` =
    let private blobs (names: string list) : Set<ContentHash> = names |> List.map hash |> Set.ofList

    [<Fact>]
    let ``Content only referenced by pruned snapshots`` () =
        // After the rows are gone only "a" is referenced, so "b" and "c" are garbage.
        let garbage = Prune.blobsToRemove (blobs [ "a"; "b"; "c" ]) (blobs [ "a" ])
        Assert.Equal<ContentHash list>([ hash "b"; hash "c" ], garbage)

    [<Fact>]
    let ``No unreferenced content to remove`` () =
        Assert.Empty(Prune.blobsToRemove (blobs [ "a"; "b" ]) (blobs [ "a"; "b"; "c" ]))

    [<Property>]
    let ``the blobs to remove never include a referenced hash`` () =
        let names = Gen.listOf (Gen.elements [ "a"; "b"; "c"; "d"; "e"; "f" ])

        Prop.forAll (Gen.zip names names |> arb) (fun (stored, referenced) ->
            let garbage = Prune.blobsToRemove (blobs stored) (blobs referenced)
            let kept = blobs referenced
            garbage |> List.forall (fun blob -> not (kept.Contains blob)))
