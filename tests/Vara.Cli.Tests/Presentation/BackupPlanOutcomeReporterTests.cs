using Spectre.Console.Testing;
using Vara.Application.Backup;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class BackupPlanOutcomeReporterTests
{
    private static TestConsole CreateConsole()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;
        return console;
    }

    [Fact]
    public void A_clean_dry_run_headline_uses_the_success_style_and_details_follow_plain()
    {
        var console = CreateConsole();

        BackupPlanOutcomeReporter.Report(console, new BackupPlanSummary(2, 1, 0, 0, 4096, []));

        Assert.Contains("\u001b[1;38;5;121mDry run - no changes were made", console.Output);
        Assert.Contains("Added:       2", console.Output);
        Assert.Contains("To transfer: 4 KB", console.Output);
        Assert.DoesNotContain("\u001b[1;38;5;186m", console.Output);
    }

    [Fact]
    public void A_dry_run_with_scan_failures_uses_the_partial_failure_style_and_lists_them()
    {
        var console = CreateConsole();

        BackupPlanOutcomeReporter.Report(console, new BackupPlanSummary(0, 0, 0, 0, 0, ["C:\\locked.txt"]));

        Assert.Contains("\u001b[1;38;5;186mDry run - no changes were made", console.Output);
        Assert.DoesNotContain("\u001b[1;38;5;121m", console.Output);
        Assert.Contains("Failed (1):", console.Output);
        Assert.Contains("C:\\locked.txt", console.Output);
    }
}
