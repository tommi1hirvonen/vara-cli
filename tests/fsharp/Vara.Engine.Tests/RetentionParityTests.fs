/// Compares the retention decision of the C# `RetentionEvaluator` with the F# `Retention.evaluate` over timestamps that
/// sit around day, ISO-Monday week, month and year boundaries and in non-zero offsets. Removed at the cutover, with the
/// C# build.
module Vara.Engine.Tests.RetentionParityTests

open System
open Xunit
open Vara.Domain

type private CsStatus = Vara.Core.Snapshots.SnapshotStatus

/// One snapshot of a generated case, for both builds.
type private Spec =
    { Id: int64
      Completed: DateTimeOffset
      Status: SnapshotStatus }

let private csStatus (status: SnapshotStatus) : CsStatus =
    match status with
    | SnapshotStatus.Running -> CsStatus.Running
    | SnapshotStatus.Complete -> CsStatus.Complete
    | SnapshotStatus.Failed -> CsStatus.Failed
    | SnapshotStatus.Cancelled -> CsStatus.Cancelled

let private csSnapshot (spec: Spec) : Vara.Core.Snapshots.Snapshot =
    Vara.Core.Snapshots.Snapshot(
        spec.Id,
        spec.Completed.AddMinutes -1.0,
        Nullable spec.Completed,
        csStatus spec.Status,
        Vara.Core.Snapshots.SnapshotStats.Empty
    )

let private fsSnapshot (spec: Spec) : SnapshotInfo =
    { SnapshotId = spec.Id
      StartedAt = spec.Completed.AddMinutes -1.0
      CompletedAt = ValueSome spec.Completed
      Status = spec.Status
      Stats =
        { FilesAdded = 0
          FilesChanged = 0
          FilesMoved = 0
          FilesDeleted = 0
          FilesFailed = 0
          BytesTransferred = 0L } }

/// The instants a case is drawn from: a second either side of the last moment of a day, a Monday, a month and a year.
/// The Sunday and the Monday of an ISO week are on either side of the week's start; the Dec 31 / Jan 1 pair of both
/// years is the year's.
let private boundaries: DateTimeOffset list =
    let around (instant: DateTimeOffset) : DateTimeOffset list =
        [ instant.AddSeconds -1.0; instant; instant.AddSeconds 1.0; instant.AddHours -12.0; instant.AddHours 12.0 ]

    [ DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero) // year
      DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) // year and day
      DateTimeOffset(2026, 3, 1, 23, 59, 59, TimeSpan.Zero) // a Sunday, the last second of an ISO week
      DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero) // the Monday that starts the next week
      DateTimeOffset(2026, 2, 28, 23, 59, 59, TimeSpan.Zero) // month
      DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
      DateTimeOffset(2024, 2, 29, 12, 0, 0, TimeSpan.Zero) // a leap day
      DateTimeOffset(2026, 6, 15, 8, 30, 0, TimeSpan.Zero) ]
    |> List.collect around
    |> List.distinct

let private offsets: TimeSpan list =
    [ -12.0; -5.0; -0.5; 0.0; 0.5; 3.0; 5.75; 14.0 ] |> List.map TimeSpan.FromHours

let private statuses: SnapshotStatus list =
    [ SnapshotStatus.Complete
      SnapshotStatus.Complete
      SnapshotStatus.Complete
      SnapshotStatus.Failed
      SnapshotStatus.Cancelled
      SnapshotStatus.Running ]

/// A case: some snapshots at distinct instants (the C# order of equal instants follows the input and the F# one follows
/// the id, and a real run cannot complete two snapshots at one instant), each in an offset of its own.
let private cases (count: int) : (Spec list * (int * int * int * int)) list =
    let random = Random 20260309

    [ for _ in 1..count do
          let size = random.Next(1, 13)
          let instants = boundaries |> List.sortBy (fun _ -> random.Next()) |> List.truncate size

          let specs =
              instants
              |> List.mapi (fun index instant ->
                  { Id = int64 (index + 1)
                    Completed = instant.ToOffset(offsets[random.Next offsets.Length])
                    Status = statuses[random.Next statuses.Length] })

          yield specs, (random.Next(0, 5), random.Next(0, 5), random.Next(0, 4), random.Next(0, 3)) ]

let private show (specs: Spec list) : string =
    specs
    |> List.map (fun spec -> $"#{spec.Id} {spec.Completed:o} {spec.Status}")
    |> String.concat "; "

[<Fact>]
let ``the kept and eligible sets of the C# evaluator and the F# one are equal`` () =
    let evaluator = Vara.Application.Retention.RetentionEvaluator()

    for specs, (daily, weekly, monthly, yearly) in cases 600 do
        let csPolicy = Vara.Core.Configuration.RetentionPolicy(daily, weekly, monthly, yearly)

        let fsPolicy: RetentionPolicy =
            { KeepDaily = (RetentionCount.tryCreate daily).Value
              KeepWeekly = (RetentionCount.tryCreate weekly).Value
              KeepMonthly = (RetentionCount.tryCreate monthly).Value
              KeepYearly = (RetentionCount.tryCreate yearly).Value }

        let csSnapshots = specs |> List.map csSnapshot
        let kept = evaluator.DetermineRetainedSnapshotIds(csSnapshots, csPolicy) |> Set.ofSeq

        let eligible =
            evaluator.DetermineEligibleForRemoval(csSnapshots, csPolicy)
            |> Seq.map (fun snapshot -> snapshot.Id)
            |> List.ofSeq
            |> List.sort

        let decision = Retention.evaluate fsPolicy (specs |> List.map fsSnapshot)
        let context = $"{show specs} with {daily}/{weekly}/{monthly}/{yearly}"
        Assert.True((kept = decision.Kept), $"kept differs for {context}: C# {kept} F# {decision.Kept}")
        Assert.True((eligible = decision.Eligible), $"eligible differs for {context}: C# {eligible} F# {decision.Eligible}")

[<Fact>]
let ``the generated cases reach every boundary kind`` () =
    // A guard on the generator: it must put instants on both sides of each boundary, in non-zero offsets.
    let utcDates = boundaries |> List.map (fun instant -> DateOnly.FromDateTime instant.UtcDateTime) |> List.distinct
    Assert.Contains(DateOnly(2025, 12, 31), utcDates)
    Assert.Contains(DateOnly(2026, 1, 1), utcDates)
    Assert.Contains(DateOnly(2026, 3, 1), utcDates)
    Assert.Contains(DateOnly(2026, 3, 2), utcDates)
    Assert.True(offsets |> List.exists (fun offset -> offset <> TimeSpan.Zero))
