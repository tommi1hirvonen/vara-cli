using Spectre.Console.Testing;
using Vara.Application.Backup;
using Vara.Cli.Presentation;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class BackupOutcomeReporterTests
{
    private static BackupRunResult MakeResult(IReadOnlyList<string> failedPaths) => new(
        SnapshotId: 1,
        StartedAt: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        CompletedAt: new DateTimeOffset(2024, 1, 1, 0, 1, 0, TimeSpan.Zero),
        Stats: new SnapshotStats(1024, 1, 0, 0, 0, failedPaths.Count),
        FailedPaths: failedPaths);

    [Fact]
    public void Clean_success_is_reported_in_the_success_style()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.Standard;

        BackupOutcomeReporter.Report(console, MakeResult([]));

        Assert.Contains("Snapshot #1 completed", console.Output);
        Assert.Contains("\u001b[1;32m", console.Output); // bold green
    }

    [Fact]
    public void Partial_failure_is_reported_in_the_partial_failure_style()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.Standard;

        BackupOutcomeReporter.Report(console, MakeResult(["C:\\file.txt"]));

        Assert.Contains("Snapshot #1 completed", console.Output);
        Assert.Contains("\u001b[1;93m", console.Output); // bold amber/yellow
        Assert.DoesNotContain("\u001b[1;32m", console.Output);
    }
}
