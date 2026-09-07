using Vara.Core.Concurrency;
using Vara.Infrastructure.Concurrency;
using Xunit;

namespace Vara.Infrastructure.Tests.Concurrency;

public class FileRunLockTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    [Fact]
    public void A_single_lock_can_be_acquired()
    {
        using var runLock = new FileRunLock();

        Assert.True(runLock.TryAcquire("files", _targetRoot));
    }

    [Fact]
    public void A_second_concurrent_acquisition_is_refused_while_the_first_is_held()
    {
        using var first = new FileRunLock();
        Assert.True(first.TryAcquire("files", _targetRoot));

        using var second = new FileRunLock();
        Assert.False(second.TryAcquire("files", _targetRoot));
    }

    [Fact]
    public void The_lock_can_be_reacquired_after_the_first_holder_disposes()
    {
        var first = new FileRunLock();
        Assert.True(first.TryAcquire("files", _targetRoot));
        first.Dispose();

        using var second = new FileRunLock();
        Assert.True(second.TryAcquire("files", _targetRoot));
    }

    [Fact]
    public void TryAcquire_throws_RunLockAccessDeniedException_when_the_lock_file_cannot_be_opened()
    {
        var varaDir = Directory.CreateDirectory(Path.Combine(_targetRoot, ".vara")).FullName;
        var lockPath = Path.Combine(varaDir, "run.lock");
        File.WriteAllText(lockPath, string.Empty);

        // Marking the lock file read-only forces the OS to deny the read-write open
        // FileRunLock performs, raising UnauthorizedAccessException without needing
        // to manipulate ACLs or run elevated.
        File.SetAttributes(lockPath, FileAttributes.ReadOnly);

        try
        {
            using var runLock = new FileRunLock();
            var ex = Assert.Throws<RunLockAccessDeniedException>(() => runLock.TryAcquire("files", _targetRoot));
            Assert.Equal(_targetRoot, ex.TargetRoot);
        }
        finally
        {
            File.SetAttributes(lockPath, FileAttributes.Normal);
        }
    }
}
