using Spectre.Console.Testing;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class WindowsTerminalProgressTests
{
    [Fact]
    public void Writes_indeterminate_determinate_and_clear_sequences()
    {
        var console = CreateLiveConsole();
        using (var progress = WindowsTerminalProgress.TryCreate(console, "test-session")!)
        {
            progress.SetIndeterminate();
            progress.SetProgress(42.4);
            progress.SetProgress(-10);
            progress.SetProgress(140);
        }

        Assert.Equal(
            "\x1b]9;4;3;0\x07" +
            "\x1b]9;4;1;42\x07" +
            "\x1b]9;4;1;0\x07" +
            "\x1b]9;4;1;100\x07" +
            "\x1b]9;4;0;0\x07",
            console.Output);
    }

    [Fact]
    public void Does_not_emit_updates_after_disposal()
    {
        var console = CreateLiveConsole();
        var progress = WindowsTerminalProgress.TryCreate(console, "test-session")!;

        progress.SetProgress(25);
        progress.Dispose();
        progress.SetProgress(75);
        progress.Dispose();

        Assert.Equal("\x1b]9;4;1;25\x07\x1b]9;4;0;0\x07", console.Output);
    }

    [Fact]
    public void Is_not_created_outside_windows_terminal_or_without_live_output()
    {
        var console = CreateLiveConsole();
        var redirectedConsole = new TestConsole();

        Assert.Null(WindowsTerminalProgress.TryCreate(console, null));
        Assert.Null(WindowsTerminalProgress.TryCreate(redirectedConsole, "test-session"));
    }

    [Fact]
    public void Rejects_non_finite_progress_values()
    {
        var console = CreateLiveConsole();
        using var progress = WindowsTerminalProgress.TryCreate(console, "test-session")!;

        Assert.Throws<ArgumentOutOfRangeException>(() => progress.SetProgress(double.NaN));
    }

    private static TestConsole CreateLiveConsole()
    {
        var console = new TestConsole();
        console.Profile.Out = new FakeTerminalOutput(console.Profile.Out.Writer);
        return console;
    }
}
