namespace Vara.Application.Reporting;

/// <summary>
/// Gatekeeps which of a rapid, potentially out-of-order stream of progress reports
/// actually gets rendered, per the progress-reporting spec's "Progress display never
/// regresses to a stale value" and "Progress display updates are rate-limited"
/// requirements. <see cref="Progress{T}"/> posts each report via the thread pool with
/// no ordering guarantee, so callers may invoke <see cref="Report"/> concurrently and
/// out of order; the admit-and-render decision below happens under a single lock so
/// only a monotonically increasing, rate-limited subset of reports is ever rendered.
/// Stateful per run - construct a fresh instance per backup run.
/// </summary>
public sealed class ProgressDisplayGate(Func<DateTimeOffset>? nowProvider = null)
{
    private static readonly TimeSpan MinRenderInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<DateTimeOffset> _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
    private readonly object _sync = new();
    private long _maxBytesRendered = -1;
    private DateTimeOffset? _lastRenderedAt;

    /// <summary>
    /// Admits and renders <paramref name="progress"/> via <paramref name="render"/> when
    /// it passes both the monotonic-value guard (its <see cref="Backup.BackupProgress.BytesTransferred"/>
    /// is not lower than the highest value already rendered) and the rate limit;
    /// otherwise the report is discarded silently. The first report of a run
    /// (<c>BytesTransferred == 0</c>) and the final report
    /// (<c>BytesTransferred == TotalBytes</c>) always render immediately regardless of
    /// the rate limit.
    /// </summary>
    public void Report(Backup.BackupProgress progress, Action<Backup.BackupProgress> render)
    {
        lock (_sync)
        {
            if (progress.BytesTransferred < _maxBytesRendered)
            {
                return;
            }

            var isFirst = progress.BytesTransferred == 0;
            var isFinal = progress.BytesTransferred >= progress.TotalBytes;
            var now = _now();
            var intervalElapsed = _lastRenderedAt is null || now - _lastRenderedAt.Value >= MinRenderInterval;

            if (!isFirst && !isFinal && !intervalElapsed)
            {
                return;
            }

            _maxBytesRendered = progress.BytesTransferred;
            _lastRenderedAt = now;
            render(progress);
        }
    }
}
