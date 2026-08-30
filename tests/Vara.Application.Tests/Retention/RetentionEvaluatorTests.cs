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
    public void Zero_count_tiers_contribute_nothing()
    {
        var snapshots = new List<Snapshot> { Completed(1, DateTimeOffset.UtcNow) };
        var policy = new RetentionPolicy(0, 0, 0, 0);

        var retained = _evaluator.DetermineRetainedSnapshotIds(snapshots, policy);

        Assert.Empty(retained);
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
