namespace Vara.Core.Backup;

/// <summary>
/// A backup run was requested for a profile while the shared per-target run lock was
/// already held by another run (backup-execution spec: "Concurrent run prevention").
/// The lock is keyed by target, not by profile, so the actual holder could be a
/// different profile that shares the same target root - the message describes the
/// target as busy rather than asserting that the requesting profile itself is the
/// one already running.
/// </summary>
public sealed class BackupAlreadyRunningException(string profileName, string targetRoot)
    : Exception(
        $"Another operation is already using target '{targetRoot}'; " +
        $"a backup run for profile '{profileName}' cannot start until it finishes.")
{
    public string ProfileName { get; } = profileName;
    public string TargetRoot { get; } = targetRoot;
}
