using Spectre.Console.Testing;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class OutcomeStyleTests
{
    [Fact]
    public void WriteLineSuccess_renders_bold_green()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.Standard;

        OutcomeStyle.WriteLineSuccess(console, "Snapshot #1 completed");

        var output = console.Output;
        Assert.Contains("Snapshot #1 completed", output);
        Assert.Contains("\u001b[1;32m", output); // bold green
    }

    [Fact]
    public void WriteLinePartialFailure_renders_bold_yellow()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.Standard;

        OutcomeStyle.WriteLinePartialFailure(console, "Snapshot #1 completed with 2 failures");

        var output = console.Output;
        Assert.Contains("Snapshot #1 completed with 2 failures", output);
        Assert.Contains("\u001b[1;93m", output); // bold yellow
    }

    [Fact]
    public void WriteLineError_renders_bold_red()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.Standard;

        OutcomeStyle.WriteLineError(console, "Error: profile not found");

        var output = console.Output;
        Assert.Contains("Error: profile not found", output);
        Assert.Contains("\u001b[1;91m", output); // bold red
    }

    [Fact]
    public void WriteLineError_does_not_throw_for_message_containing_markup_like_brackets()
    {
        var console = new TestConsole();

        var exception = Record.Exception(() =>
            OutcomeStyle.WriteLineError(console, "Error: path 'C:\\[backup]\\file.txt' not found"));

        Assert.Null(exception);
        Assert.Contains("C:\\[backup]\\file.txt", console.Output);
    }
}
