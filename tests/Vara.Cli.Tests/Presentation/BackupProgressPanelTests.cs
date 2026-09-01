using Spectre.Console.Testing;
using Vara.Application.Backup;
using Vara.Application.Reporting;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class BackupProgressPanelTests
{
    [Fact]
    public void Stats_row_fields_do_not_shift_when_a_value_crosses_a_digit_count_boundary()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // A short elapsed time against a small transferred amount produces a short,
        // single-digit throughput rendering (e.g. "5 B/s").
        var shortThroughputConsole = new TestConsole();
        shortThroughputConsole.Profile.Width = 100;
        var shortThroughputCalculator = new BackupProgressCalculator(() => start.AddSeconds(1));
        var panel1 = new BackupProgressPanel(shortThroughputCalculator);
        panel1.Update(new BackupProgress(5, 10_000));
        shortThroughputConsole.Write(panel1);
        var etaIndex1 = shortThroughputConsole.Lines[1].IndexOf("ETA", StringComparison.Ordinal);

        // A larger transferred amount over the same elapsed time produces a much
        // longer throughput rendering (e.g. "47.68 MB/s").
        var longThroughputConsole = new TestConsole();
        longThroughputConsole.Profile.Width = 100;
        var longThroughputCalculator = new BackupProgressCalculator(() => start.AddSeconds(1));
        var panel2 = new BackupProgressPanel(longThroughputCalculator);
        panel2.Update(new BackupProgress(50_000_000, 100_000_000));
        longThroughputConsole.Write(panel2);
        var etaIndex2 = longThroughputConsole.Lines[1].IndexOf("ETA", StringComparison.Ordinal);

        Assert.True(etaIndex1 >= 0);
        Assert.Equal(etaIndex1, etaIndex2);
    }

    [Fact]
    public void Bar_and_stats_render_on_separate_lines()
    {
        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var panel = new BackupProgressPanel(calculator);
        panel.Update(new BackupProgress(50, 100));

        var console = new TestConsole();
        console.Profile.Width = 80;
        console.Write(panel);

        Assert.Equal(2, console.Lines.Count);
        Assert.Contains('%', console.Lines[0]);
        Assert.DoesNotContain('%', console.Lines[1]);
    }
}
