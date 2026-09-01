using Spectre.Console.Testing;
using Vara.Application.Retention;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class PruneOutcomeReporterTests
{
    [Fact]
    public void Report_renders_in_the_success_style()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        PruneOutcomeReporter.Report(console, new PruneResult(2, 5));

        Assert.Contains("Removed 2 snapshot(s) and 5 unreferenced content blob(s).", console.Output);
        Assert.Contains("\u001b[1;38;5;121m", console.Output); // bold PaleGreen1
    }
}
