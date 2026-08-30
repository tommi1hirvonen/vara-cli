namespace Vara.Core.Abstractions;

/// <summary>
/// Prevents two backup runs from executing concurrently against the same profile's target.
/// </summary>
public interface IRunLock : IDisposable
{
    /// <summary>
    /// Attempts to acquire the run lock for the given profile/target. Returns <c>false</c>
    /// (without throwing) if another run already holds it.
    /// </summary>
    bool TryAcquire(string profileName, string targetRoot);
}
