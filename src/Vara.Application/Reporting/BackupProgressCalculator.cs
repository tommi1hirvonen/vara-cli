namespace Vara.Application.Reporting;

/// <summary>
/// A point-in-time view of a backup run's progress, derived from a raw
/// <see cref="Backup.BackupProgress"/> report plus elapsed-time-based throughput/ETA.
/// </summary>
public sealed record ProgressSnapshot(
    long BytesTransferred,
    long TotalBytes,
    double PercentComplete,
    double ThroughputBytesPerSecond,
    TimeSpan? EstimatedTimeRemaining);

/// <summary>
/// Converts raw byte-progress reports into percentage/throughput/ETA, per the
/// progress-reporting spec's "Byte-based progress" and "Throughput and ETA reporting"
/// requirements. Stateful per run (tracks the run's start time to compute a rolling
/// average throughput) - construct a fresh instance per backup run.
/// </summary>
public sealed class BackupProgressCalculator(Func<DateTimeOffset>? nowProvider = null)
{
    private readonly Func<DateTimeOffset> _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset? _startedAt;

    public ProgressSnapshot Calculate(Backup.BackupProgress progress)
    {
        var now = _now();
        _startedAt ??= now;

        var elapsedSeconds = (now - _startedAt.Value).TotalSeconds;
        var throughput = elapsedSeconds > 0 ? progress.BytesTransferred / elapsedSeconds : 0;

        // A run with nothing to transfer is immediately 100% complete, rather than an
        // undefined/divide-by-zero state.
        var percent = progress.TotalBytes > 0 ? (double)progress.BytesTransferred / progress.TotalBytes * 100 : 100;

        TimeSpan? eta = null;
        if (throughput > 0)
        {
            var remainingBytes = progress.TotalBytes - progress.BytesTransferred;
            eta = TimeSpan.FromSeconds(Math.Max(0, remainingBytes / throughput));
        }

        return new ProgressSnapshot(progress.BytesTransferred, progress.TotalBytes, percent, throughput, eta);
    }
}
