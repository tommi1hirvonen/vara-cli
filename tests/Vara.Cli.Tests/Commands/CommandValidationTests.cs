using Spectre.Console.Testing;
using Vara.Cli.Commands;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class CommandValidationTests
{
    private readonly TestConsole _errors = new();

    [Fact]
    public void ExactlyOne_accepts_either_option_alone()
    {
        Assert.Null(CommandValidation.ExactlyOne(true, false, "--at <date>", "--version <id>", _errors));
        Assert.Null(CommandValidation.ExactlyOne(false, true, "--at <date>", "--version <id>", _errors));
        Assert.Equal(string.Empty, _errors.Output);
    }

    [Fact]
    public void ExactlyOne_rejects_neither_and_names_both_options_with_their_usage()
    {
        var result = CommandValidation.ExactlyOne(false, false, "--at <date>", "--version <id>", _errors);

        Assert.Equal(1, result);
        Assert.Contains("Error: specify either --at <date> or --version <id>.", _errors.Output);
    }

    [Fact]
    public void ExactlyOne_rejects_both_and_names_only_the_option_names()
    {
        var result = CommandValidation.ExactlyOne(true, true, "--at <date>", "--version <id>", _errors);

        Assert.Equal(1, result);
        Assert.Contains("Error: --at and --version are mutually exclusive.", _errors.Output);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MutuallyExclusive_accepts_anything_but_both(bool first, bool second)
    {
        Assert.Null(CommandValidation.MutuallyExclusive(first, second, "--a", "--b", _errors));
        Assert.Equal(string.Empty, _errors.Output);
    }

    [Fact]
    public void MutuallyExclusive_rejects_both()
    {
        var result = CommandValidation.MutuallyExclusive(true, true, "--a", "--b", _errors);

        Assert.Equal(1, result);
        Assert.Contains("Error: --a and --b are mutually exclusive.", _errors.Output);
    }
}
