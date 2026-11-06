using Vara.Application.Backup;
using Vara.Application.Reporting;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class BackupProgressRenderLoopTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    // Each gate decision sees a clock a second later, so the rate limit never discards a report.
    private static ProgressDisplayGate UnthrottledGate()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new ProgressDisplayGate(() => now += TimeSpan.FromSeconds(1), renderFileCountAdvancesImmediately: false);
    }

    private static BackupProgress Progress(long bytes, long total = 1000) => new(bytes, total, 0, 1);

    [Fact]
    public void Reports_from_one_producer_render_in_order()
    {
        var rendered = new List<long>();
        var loop = new BackupProgressRenderLoop(UnthrottledGate(), p => rendered.Add(p.BytesTransferred));

        foreach (var bytes in new long[] { 0, 100, 200, 300, 1000 })
        {
            loop.Report(Progress(bytes));
        }

        Assert.True(loop.Close(Generous));

        Assert.Equal(new long[] { 0, 100, 200, 300, 1000 }, rendered);
    }

    [Fact]
    public void A_burst_of_refresh_requests_evaluates_only_the_latest_capture_once()
    {
        using var firstRenderStarted = new ManualResetEventSlim(false);
        using var releaseFirstRender = new ManualResetEventSlim(false);
        var evaluated = new List<string>();
        var refreshed = new List<IReadOnlyList<string>>();
        var loop = new BackupProgressRenderLoop(
            UnthrottledGate(),
            _ =>
            {
                firstRenderStarted.Set();
                releaseFirstRender.Wait();
            },
            refreshed.Add);

        // Block the consumer so the whole burst below is buffered as one batch.
        loop.Report(Progress(0));
        Assert.True(firstRenderStarted.Wait(Generous));

        foreach (var name in new[] { "first", "intermediate", "latest" })
        {
            loop.RequestActivePathsRefresh(() =>
            {
                evaluated.Add(name);
                return [name];
            });
        }

        releaseFirstRender.Set();
        Assert.True(loop.Close(Generous));

        Assert.Equal(new[] { "latest" }, evaluated);
        Assert.Equal(new[] { "latest" }, refreshed.Select(paths => paths.Single()));
    }

    [Fact]
    public void A_short_transfer_may_be_omitted_when_start_and_finish_coalesce()
    {
        using var firstRenderStarted = new ManualResetEventSlim(false);
        using var releaseFirstRender = new ManualResetEventSlim(false);
        var activePaths = Array.Empty<string>();
        var refreshed = new List<IReadOnlyList<string>>();
        var loop = new BackupProgressRenderLoop(
            UnthrottledGate(),
            _ =>
            {
                firstRenderStarted.Set();
                releaseFirstRender.Wait();
            },
            refreshed.Add);

        loop.Report(Progress(0));
        Assert.True(firstRenderStarted.Wait(Generous));

        activePaths = ["short.txt"];
        loop.RequestActivePathsRefresh(() => activePaths);
        activePaths = [];
        loop.RequestActivePathsRefresh(() => activePaths);

        releaseFirstRender.Set();
        Assert.True(loop.Close(Generous));

        Assert.DoesNotContain(refreshed.SelectMany(paths => paths), path => path == "short.txt");
        Assert.Empty(refreshed[^1]);
    }

    [Fact]
    public void Producers_do_not_block_and_captures_and_renders_run_on_the_consumer()
    {
        var producerThread = Environment.CurrentManagedThreadId;
        var captureThreads = new List<int>();
        var renderThreads = new List<int>();
        var refreshThreads = new List<int>();
        var loop = new BackupProgressRenderLoop(
            UnthrottledGate(),
            _ => renderThreads.Add(Environment.CurrentManagedThreadId),
            _ => refreshThreads.Add(Environment.CurrentManagedThreadId));

        loop.Report(Progress(0));
        loop.RequestActivePathsRefresh(() =>
        {
            captureThreads.Add(Environment.CurrentManagedThreadId);
            return ["a.txt"];
        });
        Assert.True(loop.Close(Generous));

        Assert.Single(captureThreads);
        Assert.NotEqual(producerThread, captureThreads[0]);
        Assert.NotEqual(producerThread, renderThreads.Single());
        Assert.NotEqual(producerThread, refreshThreads.Single());
        Assert.Equal(captureThreads[0], refreshThreads[0]);
    }

    [Fact]
    public void Close_waits_for_a_delayed_final_report_up_to_the_bound()
    {
        var delivered = false;
        using var release = new ManualResetEventSlim(false);
        var loop = new BackupProgressRenderLoop(UnthrottledGate(), _ =>
        {
            release.Wait();
            delivered = true;
        });

        loop.Report(Progress(1000));
        _ = Task.Run(() =>
        {
            Thread.Sleep(200);
            release.Set();
        });

        var drained = loop.Close(Generous);

        Assert.True(drained);
        Assert.True(delivered, "expected Close to wait for the delayed report");
    }

    [Fact]
    public void Close_returns_false_without_hanging_when_a_callback_blocks_past_the_bound()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var finalRenderCount = 0;
        var loop = new BackupProgressRenderLoop(UnthrottledGate(), _ =>
        {
            entered.Set();
            release.Wait();
        });

        try
        {
            loop.Report(Progress(1000));
            Assert.True(entered.Wait(Generous));

            var drained = loop.Close(TimeSpan.FromMilliseconds(100), () => finalRenderCount++);

            Assert.False(drained);
            Assert.Equal(0, finalRenderCount);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void FinalRender_runs_exactly_once_even_when_Close_is_called_again()
    {
        var finalRenderCount = 0;
        var loop = new BackupProgressRenderLoop(UnthrottledGate(), _ => { });

        Assert.True(loop.Close(Generous, () => finalRenderCount++));
        Assert.True(loop.Close(Generous, () => finalRenderCount++));

        Assert.Equal(1, finalRenderCount);
    }

    [Fact]
    public void No_render_callback_runs_after_Close_returns()
    {
        var renderCount = 0;
        var refreshCount = 0;
        var loop = new BackupProgressRenderLoop(
            UnthrottledGate(),
            _ => Interlocked.Increment(ref renderCount),
            _ => Interlocked.Increment(ref refreshCount));

        loop.Report(Progress(0));
        Assert.True(loop.Close(Generous));
        var renderedAtClose = Volatile.Read(ref renderCount);

        loop.Report(Progress(500));
        loop.RequestActivePathsRefresh(() => ["too-late.txt"]);
        Thread.Sleep(100);

        Assert.Equal(renderedAtClose, Volatile.Read(ref renderCount));
        Assert.Equal(0, Volatile.Read(ref refreshCount));
    }

    [Fact]
    public void No_render_callback_runs_after_a_timed_out_Close_returns()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var renderCount = 0;
        var loop = new BackupProgressRenderLoop(UnthrottledGate(), _ =>
        {
            Interlocked.Increment(ref renderCount);
            entered.Set();
            release.Wait();
        });

        loop.Report(Progress(0));
        Assert.True(entered.Wait(Generous));
        loop.Report(Progress(100));
        loop.Report(Progress(1000));

        Assert.False(loop.Close(TimeSpan.FromMilliseconds(100)));
        release.Set();
        Thread.Sleep(200);

        Assert.Equal(1, Volatile.Read(ref renderCount));
    }

    [Fact]
    public void A_throwing_progress_render_is_rethrown_and_stops_intake()
    {
        var renderCount = 0;
        var failure = new InvalidOperationException("render failed");
        var loop = new BackupProgressRenderLoop(UnthrottledGate(), _ =>
        {
            Interlocked.Increment(ref renderCount);
            throw failure;
        });

        loop.Report(Progress(0));
        Assert.True(loop.Close(Generous));
        loop.Report(Progress(500));
        loop.Report(Progress(1000));

        var thrown = Assert.Throws<InvalidOperationException>(loop.ThrowIfFaulted);

        Assert.Same(failure, thrown);
        Assert.True(loop.IsFaulted);
        Assert.Equal(1, Volatile.Read(ref renderCount));
    }

    [Fact]
    public void A_throwing_active_path_refresh_is_rethrown()
    {
        var failure = new InvalidOperationException("refresh failed");
        var loop = new BackupProgressRenderLoop(UnthrottledGate(), _ => { }, _ => throw failure);

        loop.RequestActivePathsRefresh(() => ["a.txt"]);
        Assert.True(loop.Close(Generous));

        var thrown = Assert.Throws<InvalidOperationException>(loop.ThrowIfFaulted);

        Assert.Same(failure, thrown);
    }

    [Fact]
    public void A_throwing_capture_is_rethrown()
    {
        var failure = new InvalidOperationException("capture failed");
        var loop = new BackupProgressRenderLoop(UnthrottledGate(), _ => { }, _ => { });

        loop.RequestActivePathsRefresh(() => throw failure);
        Assert.True(loop.Close(Generous));

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(loop.ThrowIfFaulted));
    }

    [Fact]
    public void ThrowIfFaulted_does_nothing_when_no_callback_failed()
    {
        var loop = new BackupProgressRenderLoop(UnthrottledGate(), _ => { });

        loop.Report(Progress(0));
        Assert.True(loop.Close(Generous));

        loop.ThrowIfFaulted();
        Assert.False(loop.IsFaulted);
    }
}
