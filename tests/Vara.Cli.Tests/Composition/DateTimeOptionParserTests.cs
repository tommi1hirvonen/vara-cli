using Vara.Cli.Composition;
using Xunit;

namespace Vara.Cli.Tests.Composition;

public class DateTimeOptionParserTests
{
    [Fact]
    public void Valid_value_parses_successfully()
    {
        var result = DateTimeOptionParser.Parse("--at", "2025-01-15");

        Assert.Equal(2025, result.Year);
        Assert.Equal(1, result.Month);
        Assert.Equal(15, result.Day);
    }

    [Fact]
    public void Invalid_value_throws_InvalidDateTimeOptionException_naming_the_option_and_value()
    {
        var ex = Assert.Throws<InvalidDateTimeOptionException>(() => DateTimeOptionParser.Parse("--at", "yesterday"));

        Assert.Equal("--at", ex.OptionName);
        Assert.Equal("yesterday", ex.Value);
    }
}
