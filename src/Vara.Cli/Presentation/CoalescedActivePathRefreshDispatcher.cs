using System.Runtime.ExceptionServices;

namespace Vara.Cli.Presentation;

/// <summary>
/// Coalesces active-path refresh requests into one running dispatch and at most one
/// pending refresh. The path provider is evaluated by the dispatch worker, not by the
/// transfer worker that raises the lifecycle notification.
/// </summary>
internal sealed class CoalescedActivePathRefreshDispatcher(Action<IReadOnlyList<string>> refresh)
{
    private readonly object _sync = new();
    private readonly object _callbackSync = new();
    private Func<IReadOnlyList<string>>? _latestCapture;
    private ExceptionDispatchInfo? _failure;
    private bool _pending;
    private bool _workerRunning;
    private volatile bool _closed;

    public void Request(Func<IReadOnlyList<string>> capture)
    {
        lock (_sync)
        {
            if (_closed || _failure is not null)
            {
                return;
            }

            _latestCapture = capture;
            _pending = true;
            if (_workerRunning)
            {
                return;
            }

            _workerRunning = true;
            if (!ThreadPool.QueueUserWorkItem(static state =>
                    ((CoalescedActivePathRefreshDispatcher)state!).ProcessRequests(), this))
            {
                _workerRunning = false;
                _pending = false;
                throw new InvalidOperationException("Unable to queue an active-path refresh.");
            }
        }
    }

    public bool TryDrain(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_sync)
        {
            while (_workerRunning)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_sync, remaining))
                {
                    return !_workerRunning;
                }
            }

            return true;
        }
    }

    public void ThrowIfFaulted()
    {
        lock (_sync)
        {
            _failure?.Throw();
        }
    }

    public void Close()
    {
        lock (_callbackSync)
        {
            _closed = true;
        }

        lock (_sync)
        {
            _pending = false;
            Monitor.PulseAll(_sync);
        }
    }

    private void ProcessRequests()
    {
        try
        {
            while (true)
            {
                Func<IReadOnlyList<string>> capture;
                lock (_sync)
                {
                    if (_closed || !_pending)
                    {
                        _workerRunning = false;
                        Monitor.PulseAll(_sync);
                        return;
                    }

                    _pending = false;
                    capture = _latestCapture!;
                }

                var paths = capture();
                lock (_callbackSync)
                {
                    if (!_closed)
                    {
                        refresh(paths);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
                _pending = false;
                _workerRunning = false;
                Monitor.PulseAll(_sync);
            }
        }
    }
}
