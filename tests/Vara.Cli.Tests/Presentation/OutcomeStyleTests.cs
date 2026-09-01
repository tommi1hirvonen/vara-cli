using Spectre.Console;
using Spectre.Console.Testing;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class OutcomeStyleTests
{
    [Fact]
    public void Success_is_bold_pastel_green()
    {
        Assert.Equal(Color.PaleGreen1, OutcomeStyle.Success.Foreground);
        Assert.Equal(Decoration.Bold, OutcomeStyle.Success.Decoration);
    }

    [Fact]
    public void PartialFailure_is_bold_pastel_amber()
    {
        Assert.Equal(Color.LightGoldenrod2, OutcomeStyle.PartialFailure.Foreground);
        Assert.Equal(Decoration.Bold, OutcomeStyle.PartialFailure.Decoration);
    }

    [Fact]
    public void Error_is_bold_pastel_red()
    {
        Assert.Equal(Color.IndianRed, OutcomeStyle.Error.Foreground);
        Assert.Equal(Decoration.Bold, OutcomeStyle.Error.Decoration);
    }

    [Fact]
    public void Neutral_is_bold_pastel_blue()
    {
        Assert.Equal(Color.LightSkyBlue1, OutcomeStyle.Neutral.Foreground);
        Assert.Equal(Decoration.Bold, OutcomeStyle.Neutral.Decoration);
    }

    [Fact]
    public void WriteLineSuccess_renders_bold_pastel_green()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        OutcomeStyle.WriteLineSuccess(console, "Snapshot #1 completed");

        var output = console.Output;
        Assert.Contains("Snapshot #1 completed", output);
        Assert.Contains("\u001b[1;38;5;121m", output); // bold PaleGreen1
    }

    [Fact]
    public void WriteLinePartialFailure_renders_bold_pastel_amber()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        OutcomeStyle.WriteLinePartialFailure(console, "Snapshot #1 completed with 2 failures");

        var output = console.Output;
        Assert.Contains("Snapshot #1 completed with 2 failures", output);
        Assert.Contains("\u001b[1;38;5;186m", output); // bold LightGoldenrod2
    }

    [Fact]
    public void WriteLineError_renders_bold_pastel_red()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        OutcomeStyle.WriteLineError(console, "Error: profile not found");

        var output = console.Output;
        Assert.Contains("Error: profile not found", output);
        Assert.Contains("\u001b[1;38;5;131m", output); // bold IndianRed
    }

    [Fact]
    public void WriteLineNeutral_renders_bold_pastel_blue()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        OutcomeStyle.WriteLineNeutral(console, "Restore cancelled: destination not overwritten.");

        var output = console.Output;
        Assert.Contains("Restore cancelled: destination not overwritten.", output);
        Assert.Contains("\u001b[1;38;5;153m", output); // bold LightSkyBlue1
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
