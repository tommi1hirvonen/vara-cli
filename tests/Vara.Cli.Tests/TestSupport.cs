using Spectre.Console;
using Spectre.Console.Testing;

namespace Vara.Cli.Tests;

/// <summary>An output that reports itself as a real terminal, so live displays are attempted.</summary>
internal sealed class FakeTerminalOutput(TextWriter writer, int width = 120) : IAnsiConsoleOutput
{
    public TextWriter Writer { get; } = writer;
    public bool IsTerminal => true;
    public int Width { get; } = width;
    public int Height => 30;

    public void SetEncoding(System.Text.Encoding encoding)
    {
    }
}

internal static class TestConsoles
{
    /// <summary>A <see cref="TestConsole"/> that <c>OutputMode.IsLiveCapable</c> treats as a terminal.</summary>
    public static TestConsole CreateLive(int width = 120)
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Out = new FakeTerminalOutput(console.Profile.Out.Writer, width);
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = width;
        return console;
    }
}
