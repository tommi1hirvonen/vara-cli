using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class CoalescedActivePathRefreshDispatcherTests
{
    [Fact]
    public async Task Requests_coalesce_to_the_latest_pending_capture_without_waiting_for_refresh()
    {
        using var firstRefreshStarted = new ManualResetEventSlim(false);
        using var releaseFirstRefresh = new ManualResetEventSlim(false);
        var refreshed = new List<IReadOnlyList<string>>();
        var refreshCount = 0;
        var dispatcher = new CoalescedActivePathRefreshDispatcher(paths =>
        {
            lock (refreshed)
            {
                refreshed.Add(paths);
            }

            if (Interlocked.Increment(ref refreshCount) == 1)
            {
                firstRefreshStarted.Set();
                releaseFirstRefresh.Wait();
            }
        });

        try
        {
            dispatcher.Request(() => ["first"]);
            Assert.True(firstRefreshStarted.Wait(TimeSpan.FromSeconds(5)), "expected the first refresh to start");

            var requests = Task.Run(() =>
            {
                dispatcher.Request(() => ["intermediate"]);
                dispatcher.Request(() => ["latest"]);
            });
            await requests.WaitAsync(TimeSpan.FromSeconds(5));

            releaseFirstRefresh.Set();
            Assert.True(dispatcher.TryDrain(TimeSpan.FromSeconds(5)));

            lock (refreshed)
            {
                Assert.Equal(new[] { "first", "latest" }, refreshed.Select(paths => paths.Single()));
            }
        }
        finally
        {
            releaseFirstRefresh.Set();
            dispatcher.Close();
        }
    }

    [Fact]
    public void A_short_transfer_may_be_omitted_when_start_and_finish_coalesce()
    {
        using var firstRefreshStarted = new ManualResetEventSlim(false);
        using var releaseFirstRefresh = new ManualResetEventSlim(false);
        var activePaths = Array.Empty<string>();
        var refreshed = new List<IReadOnlyList<string>>();
        var dispatcher = new CoalescedActivePathRefreshDispatcher(paths =>
        {
            lock (refreshed)
            {
                refreshed.Add(paths);
            }

            if (paths.Contains("priming.txt"))
            {
                firstRefreshStarted.Set();
                releaseFirstRefresh.Wait();
            }
        });

        try
        {
            dispatcher.Request(() => ["priming.txt"]);
            Assert.True(firstRefreshStarted.Wait(TimeSpan.FromSeconds(5)), "expected the first refresh to start");

            activePaths = ["short.txt"];
            dispatcher.Request(() => activePaths);
            activePaths = [];
            dispatcher.Request(() => activePaths);

            releaseFirstRefresh.Set();
            Assert.True(dispatcher.TryDrain(TimeSpan.FromSeconds(5)));

            lock (refreshed)
            {
                Assert.DoesNotContain(refreshed.SelectMany(paths => paths), path => path == "short.txt");
                Assert.Empty(refreshed[^1]);
            }
        }
        finally
        {
            releaseFirstRefresh.Set();
            dispatcher.Close();
        }
    }

    [Fact]
    public async Task Close_prevents_queued_refreshes_from_updating_the_display_after_teardown()
    {
        using var refreshStarted = new ManualResetEventSlim(false);
        using var releaseRefresh = new ManualResetEventSlim(false);
        var refreshCount = 0;
        var dispatcher = new CoalescedActivePathRefreshDispatcher(_ =>
        {
            Interlocked.Increment(ref refreshCount);
            refreshStarted.Set();
            releaseRefresh.Wait();
        });

        try
        {
            dispatcher.Request(() => ["active.txt"]);
            Assert.True(refreshStarted.Wait(TimeSpan.FromSeconds(5)), "expected the refresh to start");
            dispatcher.Request(() => Array.Empty<string>());

            var close = Task.Run(dispatcher.Close);
            releaseRefresh.Set();
            await close.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(dispatcher.TryDrain(TimeSpan.FromSeconds(5)));

            var countAfterClose = Volatile.Read(ref refreshCount);
            dispatcher.Request(() => ["too-late.txt"]);

            Assert.Equal(countAfterClose, Volatile.Read(ref refreshCount));
        }
        finally
        {
            releaseRefresh.Set();
            dispatcher.Close();
        }
    }
}
