namespace Vara.Core.Abstractions;

/// <summary>
/// Prevents two runs (backup or prune) from executing concurrently against the same
/// target root. The lock is shared across both backup and prune runs and is keyed by
/// target, not by profile.
/// </summary>
public interface IRunLock : IDisposable
{
    /// <summary>
    /// Attempts to acquire the run lock for the given profile/target. Returns <c>false</c>
    /// (without throwing) if another run already holds it.
    /// </summary>
    bool TryAcquire(string profileName, string targetRoot);
}
