using Vara.Application.Integrity;
using Xunit;

namespace Vara.Application.Tests.Integrity;

public class IntegrityCheckProgressCalculatorTests
{
    private readonly IntegrityCheckProgressCalculator _calculator = new();

    [Fact]
    public void Full_mode_combines_remaining_read_time_and_per_blob_overhead()
    {
        var progress = new IntegrityCheckProgress(
            BlobsChecked: 1,
            TotalBlobs: 3,
            BytesChecked: 100,
            TotalBytes: 300,
            BytesRead: 100,
            RemainingPresentBytes: 100,
            Elapsed: TimeSpan.FromSeconds(4),
            HashingElapsed: TimeSpan.FromSeconds(2));

        var snapshot = _calculator.Calculate(progress, quick: false);

        Assert.Equal(100d / 3, snapshot.PercentComplete, precision: 5);
        Assert.Equal(TimeSpan.FromSeconds(6), snapshot.EstimatedTimeRemaining);
    }

    [Fact]
    public void Quick_mode_estimates_from_observed_blob_time()
    {
        var progress = new IntegrityCheckProgress(
            BlobsChecked: 2,
            TotalBlobs: 4,
            BytesChecked: 0,
            TotalBytes: 0,
            BytesRead: 0,
            RemainingPresentBytes: 0,
            Elapsed: TimeSpan.FromSeconds(6),
            HashingElapsed: TimeSpan.Zero);

        var snapshot = _calculator.Calculate(progress, quick: true);

        Assert.Equal(50, snapshot.PercentComplete);
        Assert.Equal(TimeSpan.FromSeconds(6), snapshot.EstimatedTimeRemaining);
    }

    [Fact]
    public void Full_mode_eta_is_unavailable_until_remaining_work_has_observed_rates()
    {
        var progress = new IntegrityCheckProgress(
            BlobsChecked: 0,
            TotalBlobs: 2,
            BytesChecked: 0,
            TotalBytes: 100,
            BytesRead: 0,
            RemainingPresentBytes: 100,
            Elapsed: TimeSpan.Zero,
            HashingElapsed: TimeSpan.Zero);

        var snapshot = _calculator.Calculate(progress, quick: false);

        Assert.Null(snapshot.EstimatedTimeRemaining);
    }

    [Fact]
    public void Empty_and_zero_size_workloads_report_complete_without_an_eta_division()
    {
        var empty = new IntegrityCheckProgress(0, 0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);
        var zeroSize = new IntegrityCheckProgress(1, 1, 0, 0, 0, 0, TimeSpan.FromSeconds(1), TimeSpan.Zero);

        var emptySnapshot = _calculator.Calculate(empty, quick: false);
        var zeroSizeSnapshot = _calculator.Calculate(zeroSize, quick: false);

        Assert.Equal(100, emptySnapshot.PercentComplete);
        Assert.Equal(TimeSpan.Zero, emptySnapshot.EstimatedTimeRemaining);
        Assert.Equal(100, zeroSizeSnapshot.PercentComplete);
        Assert.Equal(TimeSpan.Zero, zeroSizeSnapshot.EstimatedTimeRemaining);
    }
}
