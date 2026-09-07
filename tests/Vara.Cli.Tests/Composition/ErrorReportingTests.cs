using Spectre.Console.Testing;
using Vara.Cli.Composition;
using Vara.Core.Configuration;
using Xunit;

namespace Vara.Cli.Tests.Composition;

public class ErrorReportingTests
{
    [Fact]
    public void Friendly_exception_is_written_to_the_given_console_in_the_error_style_with_exit_code_1()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        var exitCode = ErrorReporting.Run(
            () => throw new ProfileConfigNotFoundException("C:\\missing\\profiles.yml"),
            console);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: Profile configuration file not found at 'C:\\missing\\profiles.yml'.", console.Output);
        Assert.Contains("\u001b[1;38;5;131m", console.Output); // bold IndianRed
    }

    [Fact]
    public void RestoreLinkedEntryException_is_reported_as_a_friendly_error_not_a_raw_stack_trace()
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var exitCode = ErrorReporting.Run(
            () => throw new Vara.Core.Snapshots.RestoreLinkedEntryException("link"),
            console);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: Cannot restore 'link': the resolved version is a symlink/junction with no stored content.", console.Output);
        Assert.DoesNotContain("Unhandled exception", console.Output);
    }

    [Fact]
    public void Successful_action_returns_exit_code_0_and_writes_nothing()
    {
        var console = new TestConsole();

        var exitCode = ErrorReporting.Run(() => { }, console);

        Assert.Equal(0, exitCode);
        Assert.Empty(console.Output);
    }

    [Fact]
    public void Unrecognized_exception_is_rendered_via_WriteException_with_exit_code_1()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        var exitCode = ErrorReporting.Run(() => throw new InvalidOperationException("boom"), console);

        Assert.Equal(1, exitCode);
        Assert.Contains("boom", console.Output);
        Assert.Contains("InvalidOperationException", console.Output);
    }

    [Fact]
    public void Recognized_exception_still_takes_the_friendly_message_path_unchanged()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        var exitCode = ErrorReporting.Run(
            () => throw new ProfileConfigNotFoundException("C:\\missing\\profiles.yml"),
            console);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: Profile configuration file not found at 'C:\\missing\\profiles.yml'.", console.Output);
        Assert.DoesNotContain("ProfileConfigNotFoundException", console.Output); // no stack trace/type name
    }

    [Fact]
    public void InvalidDateTimeOptionException_takes_the_friendly_message_path()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        var exitCode = ErrorReporting.Run(
            () => throw new InvalidDateTimeOptionException("--at", "yesterday"),
            console);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: Invalid value 'yesterday' for option '--at'", console.Output);
        Assert.DoesNotContain("InvalidDateTimeOptionException", console.Output);
    }

    [Fact]
    public void RestoreDirectoryConfirmationRequired_takes_the_friendly_message_path()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        var exitCode = ErrorReporting.Run(
            () => throw new Vara.Core.Snapshots.RestoreDirectoryConfirmationRequiredException(@"src\docs", 3, 1),
            console);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: Restoring directory 'src\\docs' will write 3 file(s) and remove 1 file(s).", console.Output);
        Assert.DoesNotContain("RestoreDirectoryConfirmationRequiredException", console.Output);
    }

    [Fact]
    public void DiffContentTooLargeException_takes_the_friendly_message_path()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        var exitCode = ErrorReporting.Run(
            () => throw new Vara.Core.Snapshots.DiffContentTooLargeException(@"src\big.log", 10 * 1024 * 1024, 12_000_000, null),
            console);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: Cannot diff 'src\\big.log'", console.Output);
        Assert.DoesNotContain("DiffContentTooLargeException", console.Output);
    }

    [Fact]
    public void DiffBinaryContentException_takes_the_friendly_message_path()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        var exitCode = ErrorReporting.Run(
            () => throw new Vara.Core.Snapshots.DiffBinaryContentException(@"src\image.png", leftIsBinary: true, rightIsBinary: false),
            console);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: Cannot diff 'src\\image.png'", console.Output);
        Assert.DoesNotContain("DiffBinaryContentException", console.Output);
    }

    [Fact]
    public void ShowBinaryContentException_takes_the_friendly_message_path()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        var exitCode = ErrorReporting.Run(
            () => throw new Vara.Core.Snapshots.ShowBinaryContentException(@"src\image.png"),
            console);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: Cannot show 'src\\image.png'", console.Output);
        Assert.DoesNotContain("ShowBinaryContentException", console.Output);
    }
}
