namespace Vara.Core.Backup;

/// <summary>
/// A prune run was requested for a profile while the shared per-target run lock was
/// already held by another run (backup or prune) - prune shares the same per-target
/// run lock as backup so the two never operate on the target concurrently. The lock
/// is keyed by target, not by profile, so the actual holder could be a different
/// profile that shares the same target root - the message describes the target as
/// busy rather than asserting that the requesting profile itself is the one already
/// running.
/// </summary>
public sealed class PruneAlreadyRunningException(string profileName, string targetRoot)
    : Exception(
        $"Another operation is already using target '{targetRoot}'; " +
        $"a prune run for profile '{profileName}' cannot start until it finishes.")
{
    public string ProfileName { get; } = profileName;
    public string TargetRoot { get; } = targetRoot;
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
