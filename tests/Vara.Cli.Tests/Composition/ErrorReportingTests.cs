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
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.Standard;

        var exitCode = ErrorReporting.Run(
            () => throw new ProfileConfigNotFoundException("C:\\missing\\profiles.yml"),
            console);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error: Profile configuration file not found at 'C:\\missing\\profiles.yml'.", console.Output);
        Assert.Contains("\u001b[1;91m", console.Output); // bold red
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
    public void Unrecognized_exception_propagates_rather_than_being_reported()
    {
        var console = new TestConsole();

        Assert.Throws<InvalidOperationException>(() =>
            ErrorReporting.Run(() => throw new InvalidOperationException("boom"), console));
    }
}
