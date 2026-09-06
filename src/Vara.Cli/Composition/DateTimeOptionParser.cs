using System.Globalization;

namespace Vara.Cli.Composition;

/// <summary>
/// Parses the string value of a date/time command option (<c>--at</c>, <c>--since</c>,
/// <c>--left-at</c>, <c>--right-at</c>), reporting a friendly <see cref="InvalidDateTimeOptionException"/>
/// instead of letting a raw <see cref="FormatException"/> propagate on invalid input.
/// </summary>
public static class DateTimeOptionParser
{
    public static DateTimeOffset Parse(string optionName, string value)
    {
        try
        {
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
        }
        catch (FormatException)
        {
            throw new InvalidDateTimeOptionException(optionName, value);
        }
    }
}
