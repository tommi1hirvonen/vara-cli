using Spectre.Console.Testing;
using Vara.Application.Backup;
using Vara.Cli.Presentation;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class BackupOutcomeReporterTests
{
    private static BackupRunResult MakeResult(IReadOnlyList<string> failedPaths, bool cancelled = false) => new(
        SnapshotId: 1,
        StartedAt: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        CompletedAt: new DateTimeOffset(2024, 1, 1, 0, 1, 0, TimeSpan.Zero),
        Stats: new SnapshotStats(1024, 1, 0, 0, 0, failedPaths.Count),
        FailedPaths: failedPaths,
        Cancelled: cancelled);

    [Fact]
    public void Clean_success_is_reported_in_the_success_style()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        BackupOutcomeReporter.Report(console, MakeResult([]));

        Assert.Contains("Snapshot #1 completed", console.Output);
        Assert.Contains("\u001b[1;38;5;121m", console.Output); // bold PaleGreen1
    }

    [Fact]
    public void Partial_failure_is_reported_in_the_partial_failure_style()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        BackupOutcomeReporter.Report(console, MakeResult(["C:\\file.txt"]));

        Assert.Contains("Snapshot #1 completed", console.Output);
        Assert.Contains("\u001b[1;38;5;186m", console.Output); // bold LightGoldenrod2
        Assert.DoesNotContain("\u001b[1;38;5;121m", console.Output);
    }

    [Fact]
    public void Only_the_headline_carries_the_severity_style_the_detail_lines_are_plain()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        BackupOutcomeReporter.Report(console, MakeResult(["C:\\file.txt"]));

        var lines = console.Output.Split('\n');
        var headlineLine = lines.First(l => l.Contains("Snapshot #1 completed"));
        var detailLine = lines.First(l => l.Contains("Added:"));

        Assert.Contains("\u001b[1;38;5;186m", headlineLine); // bold LightGoldenrod2 on the headline
        Assert.DoesNotContain("\u001b[", detailLine); // detail lines render in the default, unstyled text
    }

    [Fact]
    public void A_gracefully_cancelled_run_is_reported_in_the_partial_failure_style_with_a_cancelled_headline()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        BackupOutcomeReporter.Report(console, MakeResult([], cancelled: true));

        // Cancelled reuses the warning style used for a partial failure, not the
        // success style, even though no paths failed - see design.md's "Cancelled
        // reuses the partial success style" decision.
        Assert.Contains("Snapshot #1 cancelled after", console.Output);
        Assert.Contains("\u001b[1;38;5;186m", console.Output); // bold LightGoldenrod2
        Assert.DoesNotContain("\u001b[1;38;5;121m", console.Output); // not the success style
    }
}
