using Spectre.Console;
using Vara.Application.Integrity;

namespace Vara.Cli.Presentation;

/// <summary>
/// Reports a completed `vara check` run's outcome in the shared success/partial-failure
/// styles, per the `cli-presentation` capability's "Outcome severity is visually
/// distinct" requirement - mirroring <see cref="PruneOutcomeReporter"/>'s style, but
/// severity here is driven purely by whether any missing or corrupt blob was found; an
/// orphaned blob is informational only and never affects the reported severity. Only
/// the summary headline carries the severity style, per the `cli-presentation`
/// capability's "severity color applies to the outcome indicator only" requirement;
/// affected-path detail lines for each finding render in the default style.
/// </summary>
public static class CheckOutcomeReporter
{
    public static void Report(IAnsiConsole console, IntegrityCheckResult result)
    {
        var hasProblems = result.Missing.Count > 0 || result.Corrupt.Count > 0;
        var headline = $"Checked {result.BlobsChecked} blob(s): {result.Missing.Count} missing, {result.Corrupt.Count} corrupt, {result.Orphaned.Count} orphaned.";

        if (hasProblems)
        {
            OutcomeStyle.WriteLinePartialFailure(console, headline);
        }
        else
        {
            OutcomeStyle.WriteLineSuccess(console, headline);
        }

        foreach (var finding in result.Missing)
        {
            WriteFinding(console, "Missing", finding);
        }

        foreach (var finding in result.Corrupt)
        {
            WriteFinding(console, "Corrupt", finding);
        }
    }

    private static void WriteFinding(IAnsiConsole console, string label, IntegrityFinding finding)
    {
        var paths = string.Join(", ", finding.AffectedPaths);
        var more = finding.TotalAffectedPathCount > finding.AffectedPaths.Count
            ? $" (+{finding.TotalAffectedPathCount - finding.AffectedPaths.Count} more)"
            : string.Empty;
        console.WriteLine($"  {label}: {finding.ContentHash} - {paths}{more}", Style.Plain);
    }
}
