using Vara.Application.Backup;
using Vara.Application.Reporting;
using Vara.Cli.Commands;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class RestoreProgressReporterTests
{
    [Fact]
    public void OnSizeResolved_renders_immediately_at_zero_bytes_against_the_known_total()
    {
        var rendered = new List<BackupProgress>();
        var reporter = new RestoreProgressReporter(new ProgressDisplayGate(), rendered.Add);

        reporter.OnSizeResolved(1000);

        var report = Assert.Single(rendered);
        Assert.Equal(0, report.BytesTransferred);
        Assert.Equal(1000, report.TotalBytes);
    }

    [Fact]
    public void OnBytesCopied_accumulates_cumulative_bytes_against_the_resolved_total()
    {
        var rendered = new List<BackupProgress>();
        var reporter = new RestoreProgressReporter(new ProgressDisplayGate(), rendered.Add);
        reporter.OnSizeResolved(30);

        reporter.OnBytesCopied(10);
        reporter.OnBytesCopied(10);
        reporter.OnBytesCopied(10);

        Assert.Equal(30, rendered[^1].BytesTransferred);
        Assert.Equal(30, rendered[^1].TotalBytes);
    }

    [Fact]
    public void Many_small_onBytesCopied_calls_do_not_redraw_on_every_single_call()
    {
        var renderCount = 0;
        var reporter = new RestoreProgressReporter(new ProgressDisplayGate(), _ => renderCount++);
        reporter.OnSizeResolved(1000);

        for (var i = 0; i < 1000; i++)
        {
            reporter.OnBytesCopied(1);
        }

        // The gate rate-limits redraws (min 100ms interval, always admitting the first and
        // final report), so 1000 one-byte chunks delivered essentially instantaneously in a
        // unit test must render far fewer than 1000 times.
        Assert.True(renderCount < 1000, $"expected fewer than 1000 renders, observed {renderCount}");
    }
}
