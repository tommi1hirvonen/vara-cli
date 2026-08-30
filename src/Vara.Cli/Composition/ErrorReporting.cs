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
    public static int Run(Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception ex) when (TryGetFriendlyMessage(ex, out var message))
        {
            Console.Error.WriteLine($"Error: {message}");
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
                or NoMatchingVersionException:
                message = ex.Message;
                return true;
            default:
                message = string.Empty;
                return false;
        }
    }
}
