namespace Vara.Application.Integrity;

/// <summary>A calculated percentage and current-run ETA for an integrity check.</summary>
public sealed record IntegrityCheckProgressSnapshot(double PercentComplete, TimeSpan? EstimatedTimeRemaining);

/// <summary>Calculates mode-specific check progress and ETA from service observations.</summary>
public sealed class IntegrityCheckProgressCalculator
{
    public IntegrityCheckProgressSnapshot Calculate(IntegrityCheckProgress progress, bool quick)
    {
        var percent = quick || progress.TotalBytes <= 0
            ? CalculatePercent(progress.BlobsChecked, progress.TotalBlobs)
            : CalculatePercent(progress.BytesChecked, progress.TotalBytes);

        return new IntegrityCheckProgressSnapshot(percent, quick ? CalculateQuickEta(progress) : CalculateFullEta(progress));
    }

    private static TimeSpan? CalculateQuickEta(IntegrityCheckProgress progress)
    {
        var remainingBlobs = Math.Max(0, progress.TotalBlobs - progress.BlobsChecked);
        if (remainingBlobs == 0)
        {
            return TimeSpan.Zero;
        }

        if (progress.BlobsChecked == 0 || progress.Elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        return TimeSpan.FromSeconds(progress.Elapsed.TotalSeconds / progress.BlobsChecked * remainingBlobs);
    }

    private static TimeSpan? CalculateFullEta(IntegrityCheckProgress progress)
    {
        var remainingBlobs = Math.Max(0, progress.TotalBlobs - progress.BlobsChecked);
        var remainingBytes = Math.Max(0, progress.RemainingPresentBytes);
        if (remainingBlobs == 0 && remainingBytes == 0)
        {
            return TimeSpan.Zero;
        }

        var elapsedOverhead = progress.Elapsed - progress.HashingElapsed;
        var overheadPerBlob = progress.BlobsChecked > 0 && elapsedOverhead > TimeSpan.Zero
            ? elapsedOverhead.TotalSeconds / progress.BlobsChecked
            : 0;
        var remainingOverhead = remainingBlobs > 0
            ? overheadPerBlob * remainingBlobs
            : 0;

        var remainingRead = 0d;
        if (remainingBytes > 0)
        {
            if (progress.BytesRead <= 0 || progress.HashingElapsed <= TimeSpan.Zero)
            {
                return null;
            }

            var bytesPerSecond = progress.BytesRead / progress.HashingElapsed.TotalSeconds;
            if (bytesPerSecond <= 0)
            {
                return null;
            }

            remainingRead = remainingBytes / bytesPerSecond;
        }

        if (remainingBlobs > 0 && overheadPerBlob <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Max(0, remainingRead + remainingOverhead));
    }

    private static double CalculatePercent(long completed, long total) =>
        total <= 0 ? 100 : Math.Clamp((double)completed / total * 100, 0, 100);
}
