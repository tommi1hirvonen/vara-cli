using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Vara.Application.Backup;
using Vara.Application.Reporting;

namespace Vara.Cli.Presentation;

/// <summary>
/// Runs one backup while funnelling its progress reports and active-path changes into an
/// unbounded channel drained by a single consumer task, so render callbacks (e.g. a Spectre
/// <c>Progress().StartAsync(ctx)</c> context) are only ever invoked from that one consumer,
/// one at a time. Producers only enqueue and never block.
/// </summary>
/// <remarks>
/// Every progress report reaches <see cref="ProgressDisplayGate"/> in channel order; only
/// the latest active-path change of each drained batch is rendered. The consumer is always
/// awaited before <see cref="RunAsync{T}"/> returns, so no render callback can run after it,
/// even when the drain bound is exceeded (the remaining queued events are then dropped).
/// </remarks>
internal static class BackupProgressRendering
{
    /// <param name="gate">Decides which reports are rendered.</param>
    /// <param name="render">Renders an admitted progress report.</param>
    /// <param name="refreshActivePaths">Renders the latest in-flight paths; <see langword="null"/> when not shown.</param>
    /// <param name="drainTimeout">How long to wait for queued events to render once the run has finished.</param>
    /// <param name="produce">Runs the backup, publishing through the supplied progress and active-path callbacks.</param>
    /// <param name="outcomeOf">Maps the run's result to the outcome to stamp when nothing failed.</param>
    /// <param name="stampOutcome">
    /// Stamps the final outcome. It runs after the consumer has finished, on success, run
    /// failure and render failure alike, so it never races a render callback.
    /// </param>
    /// <returns>The run's result, or throws the run's failure, or else a render callback's failure.</returns>
    public static async Task<T> RunAsync<T>(
        ProgressDisplayGate gate,
        Action<BackupProgress> render,
        Action<IReadOnlyList<string>>? refreshActivePaths,
        TimeSpan drainTimeout,
        Func<IProgress<BackupProgress>, Action<IReadOnlyList<string>>, Task<T>> produce,
        Func<T, BackupProgressOutcome> outcomeOf,
        Action<BackupProgressOutcome>? stampOutcome = null)
    {
        var events = Channel.CreateUnbounded<DisplayEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        using var stop = new CancellationTokenSource();
        var consumer = Task.Run(() => ConsumeAsync(events.Reader, events.Writer, gate, render, refreshActivePaths, stop.Token));

        T result = default!;
        ExceptionDispatchInfo? runFailure = null;
        try
        {
            result = await produce(
                new ChannelProgress(events.Writer),
                paths => events.Writer.TryWrite(new ActivePathsChanged(paths)));
        }
        catch (Exception exception)
        {
            runFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            events.Writer.TryComplete();
        }

        Exception? renderFault;
        try
        {
            renderFault = await consumer.WaitAsync(drainTimeout);
        }
        catch (TimeoutException)
        {
            // The deadline passed with events still queued: drop them. The consumer checks
            // the flag between callbacks, so only a callback already in progress is awaited.
            stop.Cancel();
            renderFault = await consumer;
        }

        var outcome = runFailure is not null || renderFault is not null
            ? BackupProgressOutcome.Error
            : outcomeOf(result);
        stampOutcome?.Invoke(outcome);

        runFailure?.Throw();
        if (renderFault is not null)
        {
            ExceptionDispatchInfo.Capture(renderFault).Throw();
        }

        return result;
    }

    // Returns the exception thrown by a render callback, or null; never throws.
    private static async Task<Exception?> ConsumeAsync(
        ChannelReader<DisplayEvent> reader,
        ChannelWriter<DisplayEvent> writer,
        ProgressDisplayGate gate,
        Action<BackupProgress> render,
        Action<IReadOnlyList<string>>? refreshActivePaths,
        CancellationToken stop)
    {
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                IReadOnlyList<string>? latestPaths = null;
                while (reader.TryRead(out var displayEvent))
                {
                    if (stop.IsCancellationRequested)
                    {
                        return null;
                    }

                    switch (displayEvent)
                    {
                        case ProgressReported reported:
                            gate.Report(reported.Progress, render);
                            break;

                        case ActivePathsChanged changed:
                            latestPaths = changed.Paths;
                            break;
                    }
                }

                if (stop.IsCancellationRequested)
                {
                    return null;
                }

                if (latestPaths is not null)
                {
                    refreshActivePaths?.Invoke(latestPaths);
                }
            }

            return null;
        }
        catch (Exception exception)
        {
            // No further events are accepted once a callback has failed.
            writer.TryComplete();
            return exception;
        }
    }

    private sealed class ChannelProgress(ChannelWriter<DisplayEvent> writer) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => writer.TryWrite(new ProgressReported(value));
    }

    private closed record DisplayEvent;

    private sealed record ProgressReported(BackupProgress Progress) : DisplayEvent;

    private sealed record ActivePathsChanged(IReadOnlyList<string> Paths) : DisplayEvent;
}
