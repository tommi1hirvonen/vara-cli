using Vara.Application.Backup;
using Vara.Application.Reporting;
using Xunit;

namespace Vara.Application.Tests.Reporting;

public class BackupProgressCalculatorTests
{
    [Fact]
    public void A_run_with_nothing_to_transfer_is_immediately_100_percent_complete()
    {
        var calculator = new BackupProgressCalculator();

        var snapshot = calculator.Calculate(new BackupProgress(0, 0));

        Assert.Equal(100, snapshot.PercentComplete);
    }

    [Fact]
    public void Percent_complete_reflects_the_proportion_of_bytes_not_files()
    {
        var calculator = new BackupProgressCalculator();

        var snapshot = calculator.Calculate(new BackupProgress(250, 1000));

        Assert.Equal(25, snapshot.PercentComplete);
    }

    [Fact]
    public void Throughput_is_computed_from_elapsed_time_since_the_first_report()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var calculator = new BackupProgressCalculator(() => current);

        calculator.Calculate(new BackupProgress(0, 1000)); // establishes the start time
        current = current.AddSeconds(2);
        var snapshot = calculator.Calculate(new BackupProgress(200, 1000));

        Assert.Equal(100, snapshot.ThroughputBytesPerSecond, precision: 3);
    }

    [Fact]
    public void Estimated_time_remaining_is_null_before_any_throughput_is_established()
    {
        var calculator = new BackupProgressCalculator();

        var snapshot = calculator.Calculate(new BackupProgress(0, 1000));

        Assert.Null(snapshot.EstimatedTimeRemaining);
    }

    [Fact]
    public void Estimated_time_remaining_reflects_remaining_bytes_over_throughput()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var calculator = new BackupProgressCalculator(() => current);

        calculator.Calculate(new BackupProgress(0, 1000));
        current = current.AddSeconds(2);
        var snapshot = calculator.Calculate(new BackupProgress(200, 1000)); // 100 bytes/sec, 800 remaining

        Assert.NotNull(snapshot.EstimatedTimeRemaining);
        Assert.Equal(8, snapshot.EstimatedTimeRemaining!.Value.TotalSeconds, precision: 1);
    }
}
