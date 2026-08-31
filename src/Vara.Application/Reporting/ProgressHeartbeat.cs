using Vara.Application.Backup;

namespace Vara.Application.Reporting;

/// <summary>
/// Remembers the most recently observed <see cref="BackupProgress"/> value and re-presents
/// it on demand via <see cref="Tick"/>, independent of whether any new byte-progress event
/// has arrived - lets a caller-driven periodic timer keep the display's throughput/ETA
/// reacting to elapsed wall-clock time even during a stalled transfer (add-progress-heartbeat
/// change's design.md), without this class itself owning a timer or thread. Stateful per run -
/// construct a fresh instance per backup run.
/// </summary>
public sealed class ProgressHeartbeat
{
    private readonly object _sync = new();
    private BackupProgress? _last;

    /// <summary>Records <paramref name="progress"/> as the most recently observed value, to be replayed by a later <see cref="Tick"/>.</summary>
    public void Update(BackupProgress progress)
    {
        lock (_sync)
        {
            _last = progress;
        }
    }

    /// <summary>
    /// Invokes <paramref name="render"/> with the most recently <see cref="Update"/>d value.
    /// A no-op if <see cref="Update"/> has never been called - there is nothing yet to
    /// re-present (e.g. a heartbeat tick that fires before the run's transfer stage begins).
    /// </summary>
    public void Tick(Action<BackupProgress> render)
    {
        BackupProgress? snapshot;
        lock (_sync)
        {
            snapshot = _last;
        }

        if (snapshot is not null)
        {
            render(snapshot);
        }
    }
}
