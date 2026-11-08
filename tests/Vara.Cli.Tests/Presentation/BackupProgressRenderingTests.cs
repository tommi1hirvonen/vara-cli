using Vara.Application.Backup;
using Vara.Application.Reporting;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class BackupProgressRenderingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    // Each gate decision sees a clock a second later, so the rate limit never discards a report.
    private static ProgressDisplayGate UnthrottledGate()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new ProgressDisplayGate(() => now += TimeSpan.FromSeconds(1), renderFileCountAdvancesImmediately: false);
    }

    private static BackupProgress Progress(long bytes, long total = 1000) => new(bytes, total, 0, 1);

    private static Task<int> RunAsync(
        Action<BackupProgress> render,
        Action<IReadOnlyList<string>>? refresh,
        Func<IProgress<BackupProgress>, Action<IReadOnlyList<string>>, Task> produce,
        TimeSpan? drainTimeout = null,
        Action<BackupProgressOutcome>? stamp = null,
        BackupProgressOutcome? outcome = null) =>
        BackupProgressRendering.RunAsync(
            UnthrottledGate(),
            render,
            refresh,
            drainTimeout ?? Generous,
            async (progress, onPaths) =>
            {
                await produce(progress, onPaths);
                return 0;
            },
            _ => outcome ?? BackupProgressOutcome.Success,
            stamp);

    [Fact]
    public async Task Reports_from_one_producer_render_in_order()
    {
        var rendered = new List<long>();

        await RunAsync(
            p => rendered.Add(p.BytesTransferred),
            null,
            (progress, _) =>
            {
                foreach (var bytes in new long[] { 0, 100, 200, 300, 1000 })
                {
                    progress.Report(Progress(bytes));
                }

                return Task.CompletedTask;
            });

        Assert.Equal(new long[] { 0, 100, 200, 300, 1000 }, rendered);
    }

    [Fact]
    public async Task A_burst_of_active_path_changes_renders_only_the_latest()
    {
        using var firstRenderStarted = new ManualResetEventSlim(false);
        using var releaseFirstRender = new ManualResetEventSlim(false);
        var refreshed = new List<IReadOnlyList<string>>();

        await RunAsync(
            _ =>
            {
                firstRenderStarted.Set();
                releaseFirstRender.Wait();
            },
            refreshed.Add,
            (progress, onPaths) =>
            {
                // Block the consumer so the whole burst below is buffered as one batch.
                progress.Report(Progress(0));
                Assert.True(firstRenderStarted.Wait(Generous));

                foreach (var name in new[] { "first", "intermediate", "latest" })
                {
                    onPaths([name]);
                }

                releaseFirstRender.Set();
                return Task.CompletedTask;
            });

        Assert.Equal(new[] { "latest" }, refreshed.Select(paths => paths.Single()));
    }

    [Fact]
    public async Task A_short_transfer_may_be_omitted_when_start_and_finish_coalesce()
    {
        using var firstRenderStarted = new ManualResetEventSlim(false);
        using var releaseFirstRender = new ManualResetEventSlim(false);
        var refreshed = new List<IReadOnlyList<string>>();

        await RunAsync(
            _ =>
            {
                firstRenderStarted.Set();
                releaseFirstRender.Wait();
            },
            refreshed.Add,
            (progress, onPaths) =>
            {
                progress.Report(Progress(0));
                Assert.True(firstRenderStarted.Wait(Generous));

                onPaths(["short.txt"]);
                onPaths([]);

                releaseFirstRender.Set();
                return Task.CompletedTask;
            });

        Assert.DoesNotContain(refreshed.SelectMany(paths => paths), path => path == "short.txt");
        Assert.Empty(refreshed[^1]);
    }

    [Fact]
    public async Task Producers_do_not_block_on_a_slow_render_and_every_callback_runs_one_at_a_time()
    {
        using var firstRenderStarted = new ManualResetEventSlim(false);
        using var releaseFirstRender = new ManualResetEventSlim(false);
        var active = 0;
        var maxActive = 0;
        var renders = 0;
        var refreshes = 0;

        void Enter()
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
        }

        await RunAsync(
            _ =>
            {
                Enter();
                if (Interlocked.Increment(ref renders) == 1)
                {
                    firstRenderStarted.Set();
                    releaseFirstRender.Wait();
                }

                Interlocked.Decrement(ref active);
            },
            _ =>
            {
                Enter();
                Interlocked.Increment(ref refreshes);
                Interlocked.Decrement(ref active);
            },
            (progress, onPaths) =>
            {
                progress.Report(Progress(0));
                Assert.True(firstRenderStarted.Wait(Generous));

                // Returns promptly even though the consumer is stuck inside the first render.
                progress.Report(Progress(500));
                onPaths(["a.txt"]);
                progress.Report(Progress(1000));

                releaseFirstRender.Set();
                return Task.CompletedTask;
            });

        Assert.Equal(1, maxActive);
        Assert.True(renders >= 2);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task The_run_waits_for_a_delayed_final_report_up_to_the_bound()
    {
        var delivered = false;
        using var release = new ManualResetEventSlim(false);

        await RunAsync(
            _ =>
            {
                release.Wait();
                delivered = true;
            },
            null,
            (progress, onPaths) =>
            {
                progress.Report(Progress(1000));
                _ = Task.Run(() =>
                {
                    Thread.Sleep(200);
                    release.Set();
                });

                return Task.CompletedTask;
            });

        Assert.True(delivered, "expected the run to wait for the delayed report");
    }

    [Fact]
    public async Task The_run_does_not_hang_when_a_callback_blocks_past_the_bound_and_drops_the_queued_reports()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var renderCount = 0;
        var stamps = new List<BackupProgressOutcome>();

        var run = RunAsync(
            _ =>
            {
                Interlocked.Increment(ref renderCount);
                entered.Set();
                release.Wait();
            },
            null,
            (progress, _) =>
            {
                progress.Report(Progress(0));
                Assert.True(entered.Wait(Generous));
                progress.Report(Progress(100));
                progress.Report(Progress(1000));
                return Task.CompletedTask;
            },
            drainTimeout: TimeSpan.FromMilliseconds(100),
            stamp: stamps.Add);

        // The run can only finish once the callback it is stuck in returns.
        await Task.Delay(300);
        Assert.False(run.IsCompleted);
        release.Set();
        await run;

        Assert.Equal(1, Volatile.Read(ref renderCount));
        Assert.Equal([BackupProgressOutcome.Success], stamps);
    }

    [Fact]
    public async Task No_callback_runs_after_the_run_returns()
    {
        var renderCount = 0;
        var refreshCount = 0;
        IProgress<BackupProgress>? capturedProgress = null;
        Action<IReadOnlyList<string>>? capturedOnPaths = null;

        await RunAsync(
            _ => Interlocked.Increment(ref renderCount),
            _ => Interlocked.Increment(ref refreshCount),
            (progress, onPaths) =>
            {
                capturedProgress = progress;
                capturedOnPaths = onPaths;
                progress.Report(Progress(0));
                return Task.CompletedTask;
            });
        var renderedAtReturn = Volatile.Read(ref renderCount);

        capturedProgress!.Report(Progress(500));
        capturedOnPaths!(["too-late.txt"]);
        await Task.Delay(100);

        Assert.Equal(renderedAtReturn, Volatile.Read(ref renderCount));
        Assert.Equal(0, Volatile.Read(ref refreshCount));
    }

    [Fact]
    public async Task A_throwing_progress_render_is_rethrown_and_stops_intake()
    {
        var renderCount = 0;
        var failure = new InvalidOperationException("render failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            _ =>
            {
                Interlocked.Increment(ref renderCount);
                throw failure;
            },
            null,
            async (progress, _) =>
            {
                progress.Report(Progress(0));
                await Task.Delay(100);
                progress.Report(Progress(500));
                progress.Report(Progress(1000));
            }));

        Assert.Same(failure, thrown);
        Assert.Equal(1, Volatile.Read(ref renderCount));
    }

    [Fact]
    public async Task A_throwing_active_path_refresh_is_rethrown()
    {
        var failure = new InvalidOperationException("refresh failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            _ => { },
            _ => throw failure,
            (_, onPaths) =>
            {
                onPaths(["a.txt"]);
                return Task.CompletedTask;
            }));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task A_run_that_renders_without_failure_completes_without_throwing()
    {
        await RunAsync(
            _ => { },
            _ => { },
            (progress, _) =>
            {
                progress.Report(Progress(0));
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task The_outcome_is_stamped_once_after_the_consumer_finished_on_success()
    {
        var renderFinished = false;
        var stampedAfterRender = false;
        var stamps = new List<BackupProgressOutcome>();

        await RunAsync(
            _ =>
            {
                Thread.Sleep(50);
                renderFinished = true;
            },
            null,
            (progress, _) =>
            {
                progress.Report(Progress(1000));
                return Task.CompletedTask;
            },
            stamp: outcome =>
            {
                stampedAfterRender = renderFinished;
                stamps.Add(outcome);
            },
            outcome: BackupProgressOutcome.PartialFailure);

        Assert.True(stampedAfterRender);
        Assert.Equal([BackupProgressOutcome.PartialFailure], stamps);
    }

    [Fact]
    public async Task A_failed_run_stamps_the_error_outcome_and_rethrows_the_run_failure()
    {
        var failure = new InvalidOperationException("run failed");
        var stamps = new List<BackupProgressOutcome>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            _ => { },
            null,
            (progress, _) =>
            {
                progress.Report(Progress(0));
                throw failure;
            },
            stamp: stamps.Add));

        Assert.Same(failure, thrown);
        Assert.Equal([BackupProgressOutcome.Error], stamps);
    }

    [Fact]
    public async Task A_render_failure_stamps_the_error_outcome()
    {
        var stamps = new List<BackupProgressOutcome>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            _ => throw new InvalidOperationException("render failed"),
            null,
            (progress, _) =>
            {
                progress.Report(Progress(0));
                return Task.CompletedTask;
            },
            stamp: stamps.Add,
            outcome: BackupProgressOutcome.Success));

        Assert.Equal([BackupProgressOutcome.Error], stamps);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
