namespace Vara.Domain

open System

/// What a retention policy decides about a list of snapshots.
type RetentionDecision =
    {
        /// The completed snapshots that some tier, or the newest-snapshot rule, keeps.
        Kept: Set<int64>
        /// The snapshots that may be removed: not kept, and not running. Ascending by id.
        Eligible: int64 list
    }

/// Tiered retention (retention-pruning: "Tiered retention evaluation"): each tier keeps the newest completed snapshot
/// of each of its newest calendar buckets, and the newest completed snapshot is always kept. A pure function of the
/// snapshot list and the policy, so the order of the input cannot change the result.
module Retention =
    /// The moment a snapshot is placed at: when it completed, or when it started if it has no completion time. The
    /// C# build uses the same, so a snapshot that never completed still lands in a bucket.
    let private momentOf (snapshot: SnapshotInfo) : DateTimeOffset =
        match snapshot.CompletedAt with
        | ValueSome completed -> completed
        | ValueNone -> snapshot.StartedAt

    /// The UTC calendar date of the snapshot's moment. Every tier uses it, so that the offset recorded with the time
    /// never moves a snapshot to another calendar day, month or year in one tier and not in another.
    let utcDateOf (snapshot: SnapshotInfo) : DateOnly = DateOnly.FromDateTime((momentOf snapshot).UtcDateTime)

    /// The bucket of each tier, as the first date of the calendar period that contains the day.
    module Bucket =
        let day (date: DateOnly) : DateOnly = date

        /// The Monday that starts the ISO-8601 week of the date: the C# build's week bucket.
        let week (date: DateOnly) : DateOnly =
            let sinceMonday = (int date.DayOfWeek - int DayOfWeek.Monday + 7) % 7
            date.AddDays -sinceMonday

        let month (date: DateOnly) : DateOnly = DateOnly(date.Year, date.Month, 1)
        let year (date: DateOnly) : DateOnly = DateOnly(date.Year, 1, 1)

    /// The completed snapshots with the date they are bucketed by, newest first. Ties are broken by id, so that equal
    /// times resolve the same way whatever order the snapshots came in.
    let private newestFirst (snapshots: SnapshotInfo list) : (int64 * DateOnly) list =
        snapshots
        |> List.filter (fun snapshot ->
            match snapshot.Status with
            | SnapshotStatus.Complete -> true
            | SnapshotStatus.Running
            | SnapshotStatus.Failed
            | SnapshotStatus.Cancelled -> false)
        |> List.sortBy (fun snapshot -> struct (-(momentOf snapshot).UtcTicks, -snapshot.SnapshotId))
        |> List.map (fun snapshot -> snapshot.SnapshotId, utcDateOf snapshot)

    /// The newest snapshot of each of the newest `count` buckets. A count of zero or less keeps nothing.
    let private tier (count: int) (bucketOf: DateOnly -> DateOnly) (ordered: (int64 * DateOnly) list) : Set<int64> =
        let rec go (seen: Set<DateOnly>) (kept: Set<int64>) (rest: (int64 * DateOnly) list) : Set<int64> =
            match rest with
            | [] -> kept
            | _ when seen.Count >= count -> kept
            | (id, date) :: tail ->
                let bucket = bucketOf date

                if seen.Contains bucket then go seen kept tail else go (seen.Add bucket) (kept.Add id) tail

        if count <= 0 then Set.empty else go Set.empty Set.empty ordered

    /// The snapshots the policy keeps.
    let kept (policy: RetentionPolicy) (snapshots: SnapshotInfo list) : Set<int64> =
        let ordered = newestFirst snapshots
        let newest = ordered |> List.truncate 1 |> List.map fst |> Set.ofList

        [ newest
          tier (RetentionCount.value policy.KeepDaily) Bucket.day ordered
          tier (RetentionCount.value policy.KeepWeekly) Bucket.week ordered
          tier (RetentionCount.value policy.KeepMonthly) Bucket.month ordered
          tier (RetentionCount.value policy.KeepYearly) Bucket.year ordered ]
        |> Set.unionMany

    /// Evaluates the policy: what is kept, and what may be removed. A running snapshot is never eligible; a failed or
    /// cancelled one is, whenever it is not kept, because only completed snapshots take part in the tiers.
    let evaluate (policy: RetentionPolicy) (snapshots: SnapshotInfo list) : RetentionDecision =
        let keptIds = kept policy snapshots

        let eligible =
            snapshots
            |> List.filter (fun snapshot ->
                match snapshot.Status with
                | SnapshotStatus.Running -> false
                | SnapshotStatus.Complete
                | SnapshotStatus.Failed
                | SnapshotStatus.Cancelled -> not (keptIds.Contains snapshot.SnapshotId))
            |> List.map _.SnapshotId
            |> List.sort

        { Kept = keptIds; Eligible = eligible }
