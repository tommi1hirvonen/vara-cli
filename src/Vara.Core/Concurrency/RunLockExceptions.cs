namespace Vara.Core.Concurrency;

/// <summary>
/// The run lock for a target root could not be acquired because opening the lock
/// file failed for a reason other than another run already holding it - for example,
/// a filesystem permission error on the <c>.vara</c> directory or lock file. This is
/// distinct from the lock already being held: a permission problem must not be
/// reported as "a run is already in progress", since the actual cause is different
/// and requires a different remedy.
/// </summary>
public sealed class RunLockAccessDeniedException(string targetRoot)
    : Exception($"The run lock for target '{targetRoot}' could not be acquired due to a permission problem.")
{
    public string TargetRoot { get; } = targetRoot;
}
