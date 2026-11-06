using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Vara.Application.Backup;
using Vara.Application.Reporting;

namespace Vara.Cli.Presentation;

/// <summary>
/// Funnels backup progress reports and active-path refresh requests from any thread into
/// one unbounded channel drained by a single background consumer, so render callbacks
/// (e.g. a Spectre <c>Progress().Start(ctx)</c> context) are only ever invoked from that
/// one consumer. Producers only enqueue and never block.
/// </summary>
/// <remarks>
/// Every progress report reaches <see cref="ProgressDisplayGate"/> in channel order; only
/// the latest active-path capture of each drained batch is evaluated, on the consumer.
/// <see cref="Close"/> is the single bounded teardown step and guarantees that no render
/// callback runs after it returns, even when the drain bound is exceeded.
/// </remarks>
internal sealed class BackupProgressRenderLoop : IProgress<BackupProgress>
{
    private readonly ProgressDisplayGate _gate;
    private readonly Action<BackupProgress> _render;
    private readonly Action<IReadOnlyList<string>>? _refreshActivePaths;
    private readonly Channel<DisplayEvent> _channel = Channel.CreateUnbounded<DisplayEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _callbackLock = new();
    private readonly Task _consumer;
    private volatile bool _closed;
    private ExceptionDispatchInfo? _fault;

    public BackupProgressRenderLoop(
        ProgressDisplayGate gate,
        Action<BackupProgress> render,
        Action<IReadOnlyList<string>>? refreshActivePaths = null)
    {
        _gate = gate;
        _render = render;
        _refreshActivePaths = refreshActivePaths;
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>Whether a render callback has thrown; no further events are accepted once it has.</summary>
    public bool IsFaulted => Volatile.Read(ref _fault) is not null;

    public void Report(BackupProgress value) =>
        _channel.Writer.TryWrite(new DisplayEvent(value, null));

    public void RequestActivePathsRefresh(Func<IReadOnlyList<string>> capture) =>
        _channel.Writer.TryWrite(new DisplayEvent(null, capture));

    /// <summary>
    /// Stops accepting events, waits up to <paramref name="drainTimeout"/> for the consumer
    /// to drain, then runs <paramref name="finalRender"/> exclusively of every render
    /// callback. No render callback runs once this returns. If the consumer is still
    /// inside a callback when the bound is exceeded, <paramref name="finalRender"/> is
    /// skipped rather than racing that callback.
    /// </summary>
    /// <returns><c>true</c> if the consumer drained within the bound.</returns>
    public bool Close(TimeSpan drainTimeout, Action? finalRender = null)
    {
        var deadline = DateTime.UtcNow + drainTimeout;
        _channel.Writer.TryComplete();
        var drained = _consumer.Wait(drainTimeout);

        var remaining = deadline - DateTime.UtcNow;
        if (!_callbackLock.TryEnter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
        {
            _closed = true;
            return false;
        }

        try
        {
            if (!_closed)
            {
                _closed = true;
                finalRender?.Invoke();
            }
        }
        finally
        {
            _callbackLock.Exit();
        }

        return drained;
    }

    /// <summary>Rethrows, with its original stack, the exception thrown by a render callback.</summary>
    public void ThrowIfFaulted() => Volatile.Read(ref _fault)?.Throw();

    private async Task ConsumeAsync()
    {
        try
        {
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                Func<IReadOnlyList<string>>? latestCapture = null;
                while (reader.TryRead(out var displayEvent))
                {
                    if (displayEvent.Progress is { } progress)
                    {
                        if (!RenderProgress(progress))
                        {
                            return;
                        }
                    }
                    else
                    {
                        latestCapture = displayEvent.Capture;
                    }
                }

                if (latestCapture is not null && !RefreshActivePaths(latestCapture))
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            Fault(exception);
        }
    }

    private bool RenderProgress(BackupProgress progress)
    {
        lock (_callbackLock)
        {
            if (_closed)
            {
                return false;
            }

            _gate.Report(progress, _render);
            return true;
        }
    }

    private bool RefreshActivePaths(Func<IReadOnlyList<string>> capture)
    {
        if (_refreshActivePaths is null)
        {
            return true;
        }

        var paths = capture();
        lock (_callbackLock)
        {
            if (_closed)
            {
                return false;
            }

            _refreshActivePaths(paths);
            return true;
        }
    }

    private void Fault(Exception exception)
    {
        Volatile.Write(ref _fault, ExceptionDispatchInfo.Capture(exception));
        _channel.Writer.TryComplete(exception);
    }

    private readonly record struct DisplayEvent(BackupProgress? Progress, Func<IReadOnlyList<string>>? Capture);
}
