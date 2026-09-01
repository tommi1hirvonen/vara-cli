using Spectre.Console;
using Vara.Cli.Presentation;
using Vara.Core.Backup;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;

namespace Vara.Cli.Composition;

/// <summary>
/// Translates the domain exceptions thrown by the Application/Core layers into a
/// short, user-facing message, so command actions can report errors cleanly instead
/// of dumping a raw stack trace.
/// </summary>
public static class ErrorReporting
{
    /// <param name="errorConsole">
    /// The console an error message is written to; defaults to <see cref="StandardError.Console"/>
    /// (real standard error). Overridable so tests can assert against a
    /// <c>TestConsole</c> instead of the real process's standard error stream.
    /// </param>
    public static int Run(Action action, IAnsiConsole? errorConsole = null)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception ex) when (TryGetFriendlyMessage(ex, out var message))
        {
            // Styled per the cli-presentation delta's "Outcome severity is visually
            // distinct" requirement, applying uniformly regardless of which command
            // raised the error. Written to standard error by default, so it stays
            // visible even when standard output is redirected.
            OutcomeStyle.WriteLineError(errorConsole ?? StandardError.Console, $"Error: {message}");
            return 1;
        }
    }

    private static bool TryGetFriendlyMessage(Exception ex, out string message)
    {
        switch (ex)
        {
            case ProfileConfigException
                or BackupAlreadyRunningException
                or PruneAlreadyRunningException
                or NoHistoryForPathException
                or NoMatchingVersionException
                or RestoreDestinationInMirrorException
                or DestinationExistsException:
                message = ex.Message;
                return true;
            default:
                message = string.Empty;
                return false;
        }
    }
}
