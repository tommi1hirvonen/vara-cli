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
