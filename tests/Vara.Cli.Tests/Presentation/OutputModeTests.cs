using Spectre.Console;
using Spectre.Console.Testing;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class OutputModeTests
{
    [Fact]
    public void Not_live_capable_when_output_is_not_a_terminal()
    {
        // TestConsole's default output is not a terminal, the same way a real
        // redirected/piped standard output would report itself.
        var console = new TestConsole();

        Assert.False(OutputMode.IsLiveCapable(console));
    }

    [Fact]
    public void Live_capable_when_output_is_a_terminal()
    {
        var console = new TestConsole();
        console.Profile.Out = new FakeTerminalOutput(console.Profile.Out.Writer);

        Assert.True(OutputMode.IsLiveCapable(console));
    }

    private sealed class FakeTerminalOutput(TextWriter writer) : IAnsiConsoleOutput
    {
        public TextWriter Writer { get; } = writer;
        public bool IsTerminal => true;
        public int Width => 120;
        public int Height => 30;

        public void SetEncoding(System.Text.Encoding encoding)
        {
        }
    }
}
