using Vara.Application.Backup;
using Vara.Application.Reporting;
using Xunit;

namespace Vara.Application.Tests.Reporting;

public class ProgressDisplayGateTests
{
    [Fact]
    public void Out_of_order_lower_value_is_discarded_after_a_higher_value_was_rendered()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gate = new ProgressDisplayGate(() => current);
        var rendered = new List<long>();

        // Advance well past the throttle interval between calls so only the
        // monotonic-value guard is under test, not the rate limit.
        gate.Report(new BackupProgress(0, 1000), p => rendered.Add(p.BytesTransferred));
        current = current.AddMilliseconds(200);
        gate.Report(new BackupProgress(500, 1000), p => rendered.Add(p.BytesTransferred));
        current = current.AddMilliseconds(200);
        gate.Report(new BackupProgress(300, 1000), p => rendered.Add(p.BytesTransferred)); // stale, arrives after 500

        Assert.Equal([0L, 500L], rendered);
    }

    [Fact]
    public void Equal_or_higher_values_are_never_blocked_by_the_monotonic_guard()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gate = new ProgressDisplayGate(() => current);
        var rendered = new List<long>();

        gate.Report(new BackupProgress(0, 1000), p => rendered.Add(p.BytesTransferred));
        current = current.AddMilliseconds(200);
        gate.Report(new BackupProgress(200, 1000), p => rendered.Add(p.BytesTransferred));
        current = current.AddMilliseconds(200);
        gate.Report(new BackupProgress(400, 1000), p => rendered.Add(p.BytesTransferred));

        Assert.Equal([0L, 200L, 400L], rendered);
    }

    [Fact]
    public void Multiple_rapid_reports_within_the_throttle_window_are_collapsed_to_a_single_render()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gate = new ProgressDisplayGate(() => current);
        var rendered = new List<long>();

        gate.Report(new BackupProgress(0, 1000), p => rendered.Add(p.BytesTransferred)); // first report, always renders
        gate.Report(new BackupProgress(100, 1000), p => rendered.Add(p.BytesTransferred)); // same instant, throttled
        gate.Report(new BackupProgress(200, 1000), p => rendered.Add(p.BytesTransferred)); // same instant, throttled

        Assert.Equal([0L], rendered);
    }

    [Fact]
    public void A_report_after_the_throttle_interval_elapses_is_admitted()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gate = new ProgressDisplayGate(() => current);
        var rendered = new List<long>();

        gate.Report(new BackupProgress(0, 1000), p => rendered.Add(p.BytesTransferred));
        gate.Report(new BackupProgress(100, 1000), p => rendered.Add(p.BytesTransferred)); // throttled
        current = current.AddMilliseconds(150);
        gate.Report(new BackupProgress(200, 1000), p => rendered.Add(p.BytesTransferred)); // interval elapsed

        Assert.Equal([0L, 200L], rendered);
    }

    [Fact]
    public void First_report_of_a_run_always_renders_immediately()
    {
        var gate = new ProgressDisplayGate();
        var rendered = new List<long>();

        gate.Report(new BackupProgress(0, 1000), p => rendered.Add(p.BytesTransferred));

        Assert.Equal([0L], rendered);
    }

    [Fact]
    public void Final_report_always_renders_immediately_even_within_the_throttle_window()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gate = new ProgressDisplayGate(() => current);
        var rendered = new List<long>();

        gate.Report(new BackupProgress(0, 1000), p => rendered.Add(p.BytesTransferred));
        gate.Report(new BackupProgress(1000, 1000), p => rendered.Add(p.BytesTransferred)); // final, same instant

        Assert.Equal([0L, 1000L], rendered);
    }
}
