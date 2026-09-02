namespace Vara.Core.Backup;

/// <summary>
/// A prune run was requested for a profile while a backup (or another prune) run for
/// the same profile was already in progress - prune shares the same per-profile run
/// lock as backup so the two never operate on the target concurrently.
/// </summary>
public sealed class PruneAlreadyRunningException(string profileName)
    : Exception($"Another operation for profile '{profileName}' is already in progress.")
{
    public string ProfileName { get; } = profileName;
}

/// <summary>
/// A prune run was requested for a profile with at least one snapshot eligible for
/// removal, while running with a non-interactive input stream and without the
/// <c>--yes</c>/<c>-y</c> override - so the destructive run cannot be confirmed
/// interactively and was not explicitly authorized. See the retention-pruning spec's
/// "Prune command" requirement.
/// </summary>
public sealed class PruneConfirmationRequiredException(string profileName, int eligibleCount)
    : Exception(
        $"Profile '{profileName}' has {eligibleCount} snapshot(s) eligible for removal. " +
        "Pass --yes (or -y) to confirm, or re-run interactively to be prompted.")
{
    public string ProfileName { get; } = profileName;
    public int EligibleCount { get; } = eligibleCount;
}
