using Spectre.Console;
using Vara.Cli.Composition;

namespace Vara.Cli.Commands;

/// <summary>
/// Option-combination checks that run before a command touches any profile or storage.
/// Each returns <see langword="null"/> when the combination is acceptable, or the exit
/// code to return after the error has been written.
/// </summary>
internal static class CommandValidation
{
    /// <param name="firstUsage">How the first option is shown in the message, for example <c>--at &lt;date&gt;</c>.</param>
    /// <param name="secondUsage">How the second option is shown in the message.</param>
    public static int? ExactlyOne(
        bool firstGiven,
        bool secondGiven,
        string firstUsage,
        string secondUsage,
        IAnsiConsole? errorConsole = null)
    {
        if (firstGiven && secondGiven)
        {
            return MutuallyExclusive(true, true, firstUsage, secondUsage, errorConsole);
        }

        return firstGiven || secondGiven
            ? null
            : ErrorReporting.Usage($"specify either {firstUsage} or {secondUsage}.", errorConsole);
    }

    public static int? MutuallyExclusive(
        bool firstGiven,
        bool secondGiven,
        string firstName,
        string secondName,
        IAnsiConsole? errorConsole = null) =>
        firstGiven && secondGiven
            ? ErrorReporting.Usage($"{OptionName(firstName)} and {OptionName(secondName)} are mutually exclusive.", errorConsole)
            : null;

    // Accepts either a bare option name or a usage string such as "--at <date>".
    private static string OptionName(string usage) => usage.Split(' ')[0];
}
