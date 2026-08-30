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
}
