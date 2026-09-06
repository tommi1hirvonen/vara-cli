using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class TrackedProgressTests
{
    [Fact]
    public void OutstandingCount_tracks_in_flight_reports_and_returns_to_zero_once_delivered()
    {
        using var release = new ManualResetEventSlim(false);
        using var handlerEntered = new ManualResetEventSlim(false);
        var progress = new TrackedProgress<int>(_ =>
        {
            handlerEntered.Set();
            release.Wait();
        });

        progress.Report(1);

        // The handler blocks on `release` until we let it go, so the report is
        // guaranteed to still be outstanding at this point.
        Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(5)), "expected the handler to have started");
        Assert.Equal(1, progress.OutstandingCount);

        release.Set();

        // TryDrain with a generous bound both waits for delivery to finish and
        // observes the count drop back to zero once it has.
        Assert.True(progress.TryDrain(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, progress.OutstandingCount);
    }

    [Fact]
    public void TryDrain_waits_for_a_delayed_final_report_up_to_the_bound_before_returning()
    {
        var delivered = false;
        using var release = new ManualResetEventSlim(false);
        var progress = new TrackedProgress<int>(_ =>
        {
            release.Wait();
            delivered = true;
        });

        progress.Report(1);

        // Release the handler shortly after Report is called, from another thread, so
        // TryDrain has to actually wait for it rather than finding it already done.
        _ = Task.Run(() =>
        {
            Thread.Sleep(200);
            release.Set();
        });

        var drained = progress.TryDrain(TimeSpan.FromSeconds(5));

        Assert.True(drained, "expected TryDrain to report a full drain within its bound");
        Assert.True(delivered, "expected teardown to wait for the delayed report before proceeding");
    }

    [Fact]
    public void TryDrain_proceeds_without_hanging_or_failing_when_its_bound_is_exceeded()
    {
        using var release = new ManualResetEventSlim(false);
        var progress = new TrackedProgress<int>(_ => release.Wait());

        progress.Report(1);

        // The handler never releases within the bound below - TryDrain must give up and
        // return rather than hang or throw, per design.md's "best-effort" decision.
        var drained = progress.TryDrain(TimeSpan.FromMilliseconds(50));

        Assert.False(drained, "expected TryDrain to report an incomplete drain once its bound was exceeded");
        Assert.True(progress.OutstandingCount > 0, "expected the report to still be outstanding");

        release.Set();
    }
}
