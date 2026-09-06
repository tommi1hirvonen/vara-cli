namespace Vara.Cli.Composition;

/// <summary>
/// A command option that accepts a date/time value (<c>--at</c>, <c>--since</c>,
/// <c>--left-at</c>, <c>--right-at</c>) was given a value that could not be parsed.
/// </summary>
public sealed class InvalidDateTimeOptionException(string optionName, string value)
    : Exception($"Invalid value '{value}' for option '{optionName}': expected a date/time (for example, '2025-01-15' or '2025-01-15 14:30').")
{
    public string OptionName { get; } = optionName;
    public string Value { get; } = value;
}
