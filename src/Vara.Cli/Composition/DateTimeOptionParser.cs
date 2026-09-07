using System.Globalization;

namespace Vara.Cli.Composition;

/// <summary>
/// Parses the string value of a date/time command option (<c>--at</c>, <c>--since</c>,
/// <c>--left-at</c>, <c>--right-at</c>), reporting a friendly <see cref="InvalidDateTimeOptionException"/>
/// instead of letting a raw <see cref="FormatException"/> propagate on invalid input.
/// </summary>
public static class DateTimeOptionParser
{
    // Only unambiguous ISO 8601 forms are accepted: date-only, date+time without an offset
    // (resolved under AssumeLocal), date+time with an explicit offset, and the round-trip
    // standard format. A space is also accepted in place of the "T" separator, matching the
    // example already documented in each date/time option's help text (e.g. '2025-01-15 14:30').
    // Locale-dependent slash-separated dates are deliberately excluded.
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:sszzz",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mmzzz",
        "yyyy-MM-dd HH:mm:sszzz",
        "o",
        "O"
    ];

    public static DateTimeOffset Parse(string optionName, string value)
    {
        if (DateTimeOffset.TryParseExact(
                value,
                Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var result))
        {
            return result;
        }

        throw new InvalidDateTimeOptionException(optionName, value);
    }
}
