using Spectre.Console;
using Vara.Application.Retention;

namespace Vara.Cli.Presentation;

/// <summary>
/// Reports a completed prune run's outcome in the shared success/partial-failure styles,
/// per the `cli-presentation` capability's "Outcome severity is visually distinct"
/// requirement. A run gracefully cancelled via Ctrl+C reuses the same warning style as a
/// run that completed with partial failures elsewhere in the CLI (see
/// <see cref="BackupOutcomeReporter"/>) - it is an expected, self-healing outcome rather
/// than a crash - rather than the hard-error style. Any other prune outcome either
/// completes (success style) or throws (handled by
/// <see cref="Vara.Cli.Composition.ErrorReporting"/>).
/// </summary>
public static class PruneOutcomeReporter
{
    public static void Report(IAnsiConsole console, PruneResult result)
    {
        var headline = $"Removed {result.SnapshotsRemoved} snapshot(s) and {result.BlobsRemoved} unreferenced content blob(s).";

        if (result.Cancelled)
        {
            OutcomeStyle.WriteLinePartialFailure(console, $"{headline} Prune cancelled: stopped early.");
        }
        else
        {
            OutcomeStyle.WriteLineSuccess(console, headline);
        }
    }
}
