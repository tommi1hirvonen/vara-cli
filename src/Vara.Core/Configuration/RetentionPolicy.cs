namespace Vara.Core.Configuration;

/// <summary>
/// A tiered snapshot retention policy: the number of most-recent snapshots to keep
/// per calendar bucket (day/week/month/year). A tier with a count of 0 retains none
/// of that granularity. All counts must be non-negative.
/// </summary>
public sealed record RetentionPolicy
{
    public RetentionPolicy(int keepDaily, int keepWeekly, int keepMonthly, int keepYearly)
    {
        if (keepDaily < 0) throw new ArgumentOutOfRangeException(nameof(keepDaily), keepDaily, "Retention counts must be non-negative.");
        if (keepWeekly < 0) throw new ArgumentOutOfRangeException(nameof(keepWeekly), keepWeekly, "Retention counts must be non-negative.");
        if (keepMonthly < 0) throw new ArgumentOutOfRangeException(nameof(keepMonthly), keepMonthly, "Retention counts must be non-negative.");
        if (keepYearly < 0) throw new ArgumentOutOfRangeException(nameof(keepYearly), keepYearly, "Retention counts must be non-negative.");

        KeepDaily = keepDaily;
        KeepWeekly = keepWeekly;
        KeepMonthly = keepMonthly;
        KeepYearly = keepYearly;
    }

    public int KeepDaily { get; }
    public int KeepWeekly { get; }
    public int KeepMonthly { get; }
    public int KeepYearly { get; }
}
