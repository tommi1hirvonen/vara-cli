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

    [Fact]
    public void Ambiguous_slash_separated_date_throws_InvalidDateTimeOptionException_naming_the_option_and_value()
    {
        var ex = Assert.Throws<InvalidDateTimeOptionException>(() => DateTimeOptionParser.Parse("--at", "01/02/2025"));

        Assert.Equal("--at", ex.OptionName);
        Assert.Equal("01/02/2025", ex.Value);
    }

    [Fact]
    public void Value_with_explicit_offset_parses_successfully()
    {
        var result = DateTimeOptionParser.Parse("--at", "2025-01-02T14:30:00+02:00");

        Assert.Equal(2025, result.Year);
        Assert.Equal(1, result.Month);
        Assert.Equal(2, result.Day);
        Assert.Equal(TimeSpan.FromHours(2), result.Offset);
    }

    [Fact]
    public void Value_without_offset_resolves_under_AssumeLocal()
    {
        var result = DateTimeOptionParser.Parse("--at", "2025-01-02T14:30:00");

        var expected = new DateTimeOffset(new DateTime(2025, 1, 2, 14, 30, 0, DateTimeKind.Local));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Space_separated_date_time_from_help_text_example_still_parses()
    {
        var result = DateTimeOptionParser.Parse("--at", "2025-01-15 14:30");

        var expected = new DateTimeOffset(new DateTime(2025, 1, 15, 14, 30, 0, DateTimeKind.Local));
        Assert.Equal(expected, result);
    }
}
