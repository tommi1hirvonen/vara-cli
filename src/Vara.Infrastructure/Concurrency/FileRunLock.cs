using Vara.Core.Abstractions;

namespace Vara.Infrastructure.Concurrency;

/// <summary>
/// Prevents concurrent backup runs against the same profile's target using an
/// exclusively-held lock file under <c>.vara\run.lock</c>. The OS releases the file
/// handle automatically if the process crashes, so a stale lock never survives past
/// the process that held it.
/// </summary>
public sealed class FileRunLock : IRunLock
{
    private FileStream? _lockStream;

    public bool TryAcquire(string profileName, string targetRoot)
    {
        var varaDir = Path.Combine(targetRoot, ".vara");
        Directory.CreateDirectory(varaDir);
        var lockPath = Path.Combine(varaDir, "run.lock");

        try
        {
            _lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _lockStream?.Dispose();
        _lockStream = null;
    }
}
