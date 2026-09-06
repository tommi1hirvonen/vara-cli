namespace Vara.Cli.Presentation;

/// <summary>
/// A drop-in <see cref="IProgress{T}"/> that - like <see cref="Progress{T}"/> in a console
/// app, which has no <see cref="SynchronizationContext"/> to capture - dispatches each
/// report via <see cref="ThreadPool.QueueUserWorkItem"/> rather than synchronously, but
/// additionally tracks how many queued reports have not yet finished delivering. This lets
/// a caller <see cref="TryDrain"/> - wait, up to a bound, for every report queued so far to
/// finish - before tearing down whatever the report's callback renders into (e.g. a
/// Spectre <c>Progress().Start(ctx)</c> live display), so a report can never fire against an
/// already torn-down context. See the fix-backup-pipeline-exception-and-progress-teardown
/// change's design.md - "Introduce an explicit 'drain' step ...".
/// </summary>
public sealed class TrackedProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly object _sync = new();
    private int _outstanding;

    /// <summary>Number of reports queued but not yet finished delivering.</summary>
    public int OutstandingCount
    {
        get
        {
            lock (_sync)
            {
                return _outstanding;
            }
        }
    }

    public void Report(T value)
    {
        lock (_sync)
        {
            _outstanding++;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                handler(value);
            }
            finally
            {
                lock (_sync)
                {
                    _outstanding--;
                    Monitor.PulseAll(_sync);
                }
            }
        });
    }

    /// <summary>
    /// Blocks until every report queued so far has finished delivering, or
    /// <paramref name="timeout"/> elapses first. Exceeding the timeout is not treated as
    /// an error - the caller should proceed with teardown regardless (per design.md's
    /// "bounded timeout ... best-effort" decision); the worst case is the pre-existing
    /// rare visual glitch this drain mostly eliminates, not a hang or a failed run.
    /// </summary>
    /// <returns><c>true</c> if every queued report had finished delivering before the
    /// timeout elapsed; <c>false</c> if the timeout was reached with reports still
    /// outstanding.</returns>
    public bool TryDrain(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_sync)
        {
            while (_outstanding > 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_sync, remaining))
                {
                    return _outstanding == 0;
                }
            }

            return true;
        }
    }
}
