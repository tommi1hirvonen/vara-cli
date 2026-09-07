using Vara.Core.Configuration;
using Vara.Core.Snapshots;

namespace Vara.Application.Retention;

/// <summary>
/// Evaluates a tiered retention policy against a profile's recorded snapshots:
/// retains, per configured tier (daily/weekly/monthly/yearly), the newest snapshot in
/// each of that tier's calendar buckets, up to the tier's configured count. The single
/// most recent completed snapshot is always retained regardless of tier configuration,
/// so snapshot history can never appear to fully vanish. Anything else not retained by
/// any tier is eligible for removal. See design.md's "Retention/prune algorithm" and
/// the retention-pruning spec's "Tiered retention evaluation".
/// </summary>
public sealed class RetentionEvaluator
{
    public IReadOnlySet<long> DetermineRetainedSnapshotIds(IReadOnlyList<Snapshot> snapshots, RetentionPolicy policy)
    {
        var completed = snapshots
            .Where(s => s.Status == SnapshotStatus.Complete)
            .OrderByDescending(Timestamp)
            .ToList();

        var retained = new HashSet<long>();

        // Always retain the newest completed snapshot, independent of tier bucket math,
        // so a fully-expiring policy (or an otherwise-unanchored newest snapshot) can
        // never remove every trace of a profile's snapshot history.
        if (completed.Count > 0)
        {
            retained.Add(completed[0].Id);
        }

        RetainNewestPerBucket(completed, policy.KeepDaily, s => Timestamp(s).UtcDateTime.Date, retained);
        RetainNewestPerBucket(completed, policy.KeepWeekly, s => WeekBucket(Timestamp(s)), retained);
        RetainNewestPerBucket(completed, policy.KeepMonthly, s => new DateTime(Timestamp(s).UtcDateTime.Year, Timestamp(s).UtcDateTime.Month, 1), retained);
        RetainNewestPerBucket(completed, policy.KeepYearly, s => new DateTime(Timestamp(s).UtcDateTime.Year, 1, 1), retained);

        return retained;
    }

    /// <summary>
    /// Snapshots that are not retained (by any configured tier, or as the newest
    /// completed snapshot) and are not currently in progress.
    /// </summary>
    public IReadOnlyList<Snapshot> DetermineEligibleForRemoval(IReadOnlyList<Snapshot> snapshots, RetentionPolicy policy)
    {
        var retained = DetermineRetainedSnapshotIds(snapshots, policy);
        return snapshots
            .Where(s => s.Status != SnapshotStatus.Running && !retained.Contains(s.Id))
            .ToList();
    }

    private static DateTimeOffset Timestamp(Snapshot snapshot) => snapshot.CompletedAt ?? snapshot.StartedAt;

    private static void RetainNewestPerBucket<TKey>(
        List<Snapshot> orderedNewestFirst, int bucketCount, Func<Snapshot, TKey> bucketOf, HashSet<long> retained)
        where TKey : notnull
    {
        if (bucketCount <= 0)
        {
            return;
        }

        var seenBuckets = new HashSet<TKey>();
        foreach (var snapshot in orderedNewestFirst)
        {
            var bucket = bucketOf(snapshot);
            if (!seenBuckets.Add(bucket))
            {
                continue; // a newer snapshot already claimed this bucket
            }

            retained.Add(snapshot.Id);

            if (seenBuckets.Count >= bucketCount)
            {
                break;
            }
        }
    }

    /// <summary>The Monday that starts the ISO-8601 week containing <paramref name="timestamp"/>, used as a stable per-week bucket key.</summary>
    private static DateTime WeekBucket(DateTimeOffset timestamp)
    {
        var date = timestamp.UtcDateTime.Date;
        var daysSinceMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-daysSinceMonday);
    }
}
