using Spectre.Console;
using Vara.Application.Backup;
using Vara.Application.Reporting;

namespace Vara.Cli.Presentation;

/// <summary>
/// Reports a completed backup run's outcome using the severity style appropriate to
/// whether any file failed or the run was gracefully cancelled via Ctrl+C, per the
/// `progress-reporting` delta's "Backup run outcome reflects failure severity"
/// requirement and backup-execution's "Graceful cancellation via Ctrl+C" requirement.
/// Only the completion headline (the run's first summary line) carries the severity
/// style; the supporting detail lines (added/changed/moved/deleted/transferred/failed)
/// render in the default style, per the `cli-presentation` capability's "severity
/// color applies to the outcome indicator only" requirement, so the summary is not
/// dominated by a single color. A hard error that prevents a run from completing at
/// all (for example, a locked profile) never reaches this reporter - it propagates
/// out to <see cref="Vara.Cli.Composition.ErrorReporting"/> instead, which applies the
/// same severity styling to that case per the `cli-presentation` capability.
/// </summary>
public static class BackupOutcomeReporter
{
    public static void Report(IAnsiConsole console, BackupRunResult result)
    {
        var summary = BackupRunSummaryFormatter.Format(result);
        var newlineIndex = summary.IndexOf('\n');
        var headline = newlineIndex >= 0 ? summary[..newlineIndex].TrimEnd('\r') : summary;
        var detail = newlineIndex >= 0 ? summary[(newlineIndex + 1)..] : null;

        if (result.FailedPaths.Count == 0 && !result.Cancelled)
        {
            OutcomeStyle.WriteLineSuccess(console, headline);
        }
        else
        {
            // A gracefully cancelled run reuses the same warning style as a run that
            // completed with partial failures - it is an expected, self-healing outcome
            // rather than a crash, so it does not warrant the hard-error style used for
            // Failed - see design.md's "Cancelled reuses the partial success style" decision.
            OutcomeStyle.WriteLinePartialFailure(console, headline);
        }

        if (detail is not null)
        {
            console.WriteLine(detail, Style.Plain);
        }
    }
}
