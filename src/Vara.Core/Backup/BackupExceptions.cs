namespace Vara.Core.Backup;

/// <summary>
/// A backup run was requested for a profile while another run for the same profile
/// was already in progress (backup-execution spec: "Concurrent run prevention").
/// </summary>
public sealed class BackupAlreadyRunningException(string profileName)
    : Exception($"A backup run for profile '{profileName}' is already in progress.")
{
    public string ProfileName { get; } = profileName;
}
