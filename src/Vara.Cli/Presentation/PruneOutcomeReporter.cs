using Spectre.Console;
using Vara.Application.Retention;

namespace Vara.Cli.Presentation;

/// <summary>
/// Reports a completed prune run's outcome in the shared success style, per the
/// `cli-presentation` capability's "Outcome severity is visually distinct" requirement.
/// A prune run has no partial-failure outcome of its own - it either completes or
/// throws (handled by <see cref="Vara.Cli.Composition.ErrorReporting"/>).
/// </summary>
public static class PruneOutcomeReporter
{
    public static void Report(IAnsiConsole console, PruneResult result) =>
        OutcomeStyle.WriteLineSuccess(
            console,
            $"Removed {result.SnapshotsRemoved} snapshot(s) and {result.BlobsRemoved} unreferenced content blob(s).");
}
