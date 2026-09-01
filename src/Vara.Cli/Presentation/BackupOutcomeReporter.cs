using Spectre.Console;
using Vara.Application.Backup;
using Vara.Application.Reporting;

namespace Vara.Cli.Presentation;

/// <summary>
/// Reports a completed backup run's outcome using the severity style appropriate to
/// whether any file failed, per the `progress-reporting` delta's "Backup run outcome
/// reflects failure severity" requirement. A hard error that prevents a run from
/// completing at all (for example, a locked profile) never reaches this reporter - it
/// propagates out to <see cref="Vara.Cli.Composition.ErrorReporting"/> instead, which
/// applies the same severity styling to that case per the `cli-presentation`
/// capability.
/// </summary>
public static class BackupOutcomeReporter
{
    public static void Report(IAnsiConsole console, BackupRunResult result)
    {
        var summary = BackupRunSummaryFormatter.Format(result);
        if (result.FailedPaths.Count == 0)
        {
            OutcomeStyle.WriteLineSuccess(console, summary);
        }
        else
        {
            OutcomeStyle.WriteLinePartialFailure(console, summary);
        }
    }
}
