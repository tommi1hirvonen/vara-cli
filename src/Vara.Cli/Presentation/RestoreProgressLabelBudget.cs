namespace Vara.Cli.Presentation;

/// <summary>
/// Derives the character budget available to a restore progress task's label, so a long
/// restored path can be pre-truncated (via <see cref="PathLabelTruncator"/>) before Spectre
/// ever measures it, per design.md's "Budget derivation" decision. Reserves space for the
/// other stock columns <c>RestoreCommand</c>'s live display places alongside the label in the
/// same row (<c>ProgressBarColumn</c>, <c>PercentageColumn</c>, <c>RemainingTimeColumn</c>,
/// <c>TransferSpeedColumn</c>), so the label's raw length can no longer starve the bar of
/// width during Spectre's row-layout pass.
/// </summary>
public static class RestoreProgressLabelBudget
{
    // Matches ProgressBarColumn's own default Width.
    private const int BarWidth = 40;

    // PercentageColumn.GetColumnWidth() and RemainingTimeColumn.GetColumnWidth() report fixed
    // widths of 4 and 8 respectively - reserved as-is, since Spectre honors a column's declared
    // GetColumnWidth exactly, unlike the auto-measured columns (label, bar, transfer speed).
    private const int PercentageWidth = 4;
    private const int RemainingTimeWidth = 8;

    // TransferSpeedColumn has no fixed GetColumnWidth (auto-measured, like the label itself),
    // but its content (for example "999.99 GB/s") is always short and roughly constant - a
    // generous upper bound reserves enough room without needing per-frame measurement.
    private const int TransferSpeedWidth = 12;

    // Inter-column padding/spacing Spectre applies across the five-column row.
    private const int ColumnSpacing = 8;

    // However narrow the terminal, the label always gets at least this many characters - a
    // short, still-legible truncated label rather than an empty or negative-width one.
    private const int MinimumLabelWidth = 20;

    /// <summary>
    /// Given the terminal's reported total width, returns the character budget the restore
    /// task label - the whole description string, not just the path portion - should be
    /// bounded to.
    /// </summary>
    public static int Compute(int terminalWidth)
    {
        var reserved = BarWidth + PercentageWidth + RemainingTimeWidth + TransferSpeedWidth + ColumnSpacing;
        return Math.Max(MinimumLabelWidth, terminalWidth - reserved);
    }
}
