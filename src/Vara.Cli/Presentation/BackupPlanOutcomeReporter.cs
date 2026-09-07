using Spectre.Console;
using Vara.Application.Backup;
using Vara.Application.Reporting;

namespace Vara.Cli.Presentation;

/// <summary>
/// Reports a dry run's planned outcome using the same severity-styled headline /
/// plain-styled detail convention as <see cref="BackupOutcomeReporter"/> - a dry run
/// with scan failures uses the partial-failure style, otherwise the success style,
/// per the backup-execution capability's "Dry-run mode" requirement.
/// </summary>
public static class BackupPlanOutcomeReporter
{
    public static void Report(IAnsiConsole console, BackupPlanSummary summary)
    {
        var text = BackupPlanSummaryFormatter.Format(summary);
        var newlineIndex = text.IndexOf('\n');
        var headline = newlineIndex >= 0 ? text[..newlineIndex].TrimEnd('\r') : text;
        var detail = newlineIndex >= 0 ? text[(newlineIndex + 1)..] : null;

        if (summary.FailedPaths.Count == 0)
        {
            OutcomeStyle.WriteLineSuccess(console, headline);
        }
        else
        {
            OutcomeStyle.WriteLinePartialFailure(console, headline);
        }

        if (detail is not null)
        {
            console.WriteLine(detail, Style.Plain);
        }
    }
}
