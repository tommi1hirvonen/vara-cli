using Vara.Application.Retention;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.Retention;

public class RetentionEvaluatorTests
{
    private readonly RetentionEvaluator _evaluator = new();

    private static Snapshot Completed(long id, DateTimeOffset at) => new(id, at, at, SnapshotStatus.Complete, SnapshotStats.Empty);

    [Fact]
    public void Only_the_newest_snapshot_of_a_day_is_retained_when_the_daily_bucket_is_full()
    {
        var day = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshots = new List<Snapshot>
        {
            Completed(1, day.AddHours(1)),
            Completed(2, day.AddHours(2)), // newest of the day
        };
        var policy = new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);

        Assert.Equal(new HashSet<long> { 2 }, retained);
    }

    [Fact]
    public void Daily_tier_retains_the_newest_per_day_up_to_the_configured_count()
    {
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var day3 = day1.AddDays(2);
        var snapshots = new List<Snapshot> { Completed(1, day1), Completed(2, day2), Completed(3, day3) };
        var policy = new RetentionPolicy(keepDaily: 2, keepWeekly: 0, keepMonthly: 0, keepYearly: 0);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);

        // Only the 2 most recent distinct days are retained (day3, day2) - day1 is not.
        Assert.Equal(new HashSet<long> { 3, 2 }, retained);
    }

    [Fact]
    public void Zero_count_tiers_contribute_nothing_beyond_the_newest_snapshot_guarantee()
    {
        var snapshots = new List<Snapshot> { Completed(1, DateTimeOffset.UtcNow) };
        var policy = new RetentionPolicy(0, 0, 0, 0);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);

        // The only snapshot present is retained solely because it's the newest
        // completed one, not because any tier contributed it.
        Assert.Equal(new HashSet<long> { 1 }, retained);
    }

    [Fact]
    public void All_zero_tier_policy_retains_only_the_newest_completed_snapshot()
    {
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var day3 = day1.AddDays(2);
        var snapshots = new List<Snapshot> { Completed(1, day1), Completed(2, day2), Completed(3, day3) };
        var policy = new RetentionPolicy(0, 0, 0, 0);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);
        var eligible = _evaluator.DetermineEligibleForRemoval(snapshots, policy);

        Assert.Equal(new HashSet<long> { 3 }, retained);
        Assert.Equal(new HashSet<long> { 1, 2 }, eligible.Select(s => s.Id).ToHashSet());
    }

    [Fact]
    public void Newest_completed_snapshot_is_retained_even_when_no_tier_bucket_math_would_have_retained_it()
    {
        // RetainNewestPerBucket always processes snapshots newest-first, so whenever any
        // tier has a non-zero count it necessarily claims a bucket for the overall newest
        // snapshot. The only configuration where tier bucket math alone would *not* have
        // retained the newest snapshot is when every tier count is 0 - so that's the
        // scenario that isolates the newest-snapshot guarantee from tier bucket math.
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var snapshots = new List<Snapshot> { Completed(1, day1), Completed(2, day2) };
        var policy = new RetentionPolicy(0, 0, 0, 0);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);

        Assert.Equal(new HashSet<long> { 2 }, retained);
    }

    [Fact]
    public void Newest_snapshot_guarantee_applies_to_the_newest_completed_snapshot_not_the_newest_overall()
    {
        var completedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var runningStartedAt = completedAt.AddHours(1); // most recent overall, but not completed
        var snapshots = new List<Snapshot>
        {
            Completed(1, completedAt),
            new(2, runningStartedAt, null, SnapshotStatus.Running, SnapshotStats.Empty),
        };
        var policy = new RetentionPolicy(0, 0, 0, 0);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);
        var eligible = _evaluator.DetermineEligibleForRemoval(snapshots, policy);

        // The running snapshot is never itself eligible for removal (it's excluded
        // regardless of retention), and the completed snapshot is retained as the
        // newest *completed* snapshot - not because it happens to be "newest overall".
        Assert.Equal(new HashSet<long> { 1 }, retained);
        Assert.Empty(eligible);
    }

    [Fact]
    public void A_snapshot_retained_by_multiple_tiers_is_only_counted_once()
    {
        var snapshots = new List<Snapshot> { Completed(1, DateTimeOffset.UtcNow) };
        var policy = new RetentionPolicy(keepDaily: 1, keepWeekly: 1, keepMonthly: 1, keepYearly: 1);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);

        Assert.Equal(new HashSet<long> { 1 }, retained);
    }

    [Fact]
    public void Monthly_tier_retains_the_newest_per_calendar_month()
    {
        var snapshots = new List<Snapshot>
        {
            Completed(1, new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)),
            Completed(2, new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero)), // newest in January
            Completed(3, new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero)), // newest in February
        };
        var policy = new RetentionPolicy(keepDaily: 0, keepWeekly: 0, keepMonthly: 2, keepYearly: 0);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);

        Assert.Equal(new HashSet<long> { 3, 2 }, retained);
    }

    [Fact]
    public void Monthly_tier_buckets_by_UTC_calendar_date_not_the_stored_offset()
    {
        // UTC instant is 2026-01-31T21:30:00Z (January), but the local calendar
        // date embedded in the offset reads as 2026-02-01 (February). The
        // monthly tier must bucket this using the UTC date, i.e. January.
        var straddling = new DateTimeOffset(2026, 2, 1, 0, 30, 0, TimeSpan.FromHours(3));
        var earlierInJanuaryUtc = new DateTimeOffset(2026, 1, 31, 10, 0, 0, TimeSpan.Zero);
        var snapshots = new List<Snapshot> { Completed(1, earlierInJanuaryUtc), Completed(2, straddling) };
        var policy = new RetentionPolicy(keepDaily: 0, keepWeekly: 0, keepMonthly: 1, keepYearly: 0);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);

        // If the monthly tier read .Year/.Month directly off the offset, the
        // straddling snapshot would land in its own February bucket and both
        // snapshots would be retained. Bucketing by UTC date puts both in the
        // same January bucket, so only the newest (id 2) is retained.
        Assert.Equal(new HashSet<long> { 2 }, retained);
    }

    [Fact]
    public void Yearly_tier_buckets_by_UTC_calendar_date_not_the_stored_offset()
    {
        // UTC instant is 2026-12-31T21:30:00Z (year 2026), but the local
        // calendar date embedded in the offset reads as 2027-01-01. The yearly
        // tier must bucket this using the UTC date, i.e. 2026.
        var straddling = new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.FromHours(3));
        var earlierInYearUtc = new DateTimeOffset(2026, 12, 31, 10, 0, 0, TimeSpan.Zero);
        var snapshots = new List<Snapshot> { Completed(1, earlierInYearUtc), Completed(2, straddling) };
        var policy = new RetentionPolicy(keepDaily: 0, keepWeekly: 0, keepMonthly: 0, keepYearly: 1);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);

        // If the yearly tier read .Year directly off the offset, the straddling
        // snapshot would land in its own 2027 bucket and both snapshots would
        // be retained. Bucketing by UTC date puts both in the same 2026
        // bucket, so only the newest (id 2) is retained.
        Assert.Equal(new HashSet<long> { 2 }, retained);
    }

    [Fact]
    public void Daily_and_monthly_tiers_agree_on_the_newest_snapshot_for_a_period_they_both_cover()
    {
        // Both snapshots fall on the same UTC calendar day (2026-01-31) even
        // though their stored offsets place their local calendar dates in
        // different months (January vs February). Before the fix, the monthly
        // tier bucketed by the offset's own .Year/.Month and could disagree
        // with the UTC-based daily tier about which snapshot is "newest in
        // the period". After the fix, both tiers agree.
        var earlierUtc = new DateTimeOffset(2026, 1, 31, 5, 0, 0, TimeSpan.Zero); // UTC day 31, month Jan
        var laterLocalNextMonth = new DateTimeOffset(2026, 2, 1, 0, 30, 0, TimeSpan.FromHours(3)); // UTC: 2026-01-31T21:30:00Z
        var snapshots = new List<Snapshot> { Completed(1, earlierUtc), Completed(2, laterLocalNextMonth) };

        var dailyPolicy = new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0);
        var monthlyPolicy = new RetentionPolicy(keepDaily: 0, keepWeekly: 0, keepMonthly: 1, keepYearly: 0);

        var dailyRetained = _evaluator.DetermineRetainedSnapshotIds(snapshots, dailyPolicy);
        var monthlyRetained = _evaluator.DetermineRetainedSnapshotIds(snapshots, monthlyPolicy);

        // Both snapshots share the same UTC day, so the daily tier retains
        // only the newest of the two (id 2). Both snapshots also share the
        // same UTC month, so the monthly tier must pick the same snapshot -
        // not a different one due to offset-based bucketing.
        Assert.Equal(new HashSet<long> { 2 }, dailyRetained);
        Assert.Equal(new HashSet<long> { 2 }, monthlyRetained);
    }

    [Fact]
    public void Snapshots_not_retained_by_any_tier_are_eligible_for_removal()
    {
        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var snapshots = new List<Snapshot> { Completed(1, day1), Completed(2, day2) };
        var policy = new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0);

        var eligible = _evaluator.DetermineEligibleForRemoval(snapshots, policy);

        var snapshot = Assert.Single(eligible);
        Assert.Equal(1, snapshot.Id);
    }

    [Fact]
    public void A_running_snapshot_is_never_eligible_for_removal()
    {
        var snapshots = new List<Snapshot> { new(1, DateTimeOffset.UtcNow, null, SnapshotStatus.Running, SnapshotStats.Empty) };
        var policy = new RetentionPolicy(0, 0, 0, 0);

        var eligible = _evaluator.DetermineEligibleForRemoval(snapshots, policy);

        Assert.Empty(eligible);
    }

    [Fact]
    public void A_failed_snapshot_not_retained_by_any_tier_is_eligible_for_removal()
    {
        var snapshots = new List<Snapshot> { new(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SnapshotStatus.Failed, SnapshotStats.Empty) };
        var policy = new RetentionPolicy(0, 0, 0, 0);

        var eligible = _evaluator.DetermineEligibleForRemoval(snapshots, policy);

        Assert.Single(eligible);
    }
}
