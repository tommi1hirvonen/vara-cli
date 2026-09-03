using Spectre.Console;
using Spectre.Console.Rendering;
using Vara.Application.Backup;
using Vara.Application.Reporting;

namespace Vara.Cli.Presentation;

/// <summary>
/// Which of the backup progress display's three synthetic <see cref="ProgressTask"/>s
/// a given task represents, stashed in that task's <see cref="ProgressTask.State"/>
/// under <see cref="BackupProgressColumn.RoleKey"/> so <see cref="BackupProgressColumn"/>
/// knows which content to render for it.
/// </summary>
public enum BackupProgressRole
{
    Scan,
    Bar,
    Stats,
}

/// <summary>
/// A bar-role or scan-role task's outcome, for coloring purposes only - stashed in that
/// task's <see cref="ProgressTask.State"/> under <see cref="BackupProgressColumn.OutcomeKey"/>.
/// <see cref="Running"/> is deliberately the enum's default (0) value, since a task's
/// <see cref="ProgressTaskState"/> returns a key's default when it was never stamped - a
/// task that hasn't had its outcome explicitly set is still running. Per design.md's
/// "one outcome enum ... read by both scan and bar rendering" decision: the byte-based
/// percentage no longer determines the bar's final color (a failure doesn't always move
/// the percentage to 100, and a Move/Delete-only failure never touches it at all), so the
/// caller (<see cref="Commands.BackupCommand"/>) stamps the actual run outcome once it is
/// known, instead.
/// </summary>
public enum BackupProgressOutcome
{
    Running,
    Success,
    PartialFailure,
    Error,
}

/// <summary>
/// A value-type snapshot of the raw byte counts a task's row should render, stashed in
/// that task's <see cref="ProgressTask.State"/> under
/// <see cref="BackupProgressColumn.ProgressKey"/>. <see cref="ProgressTaskState"/> only
/// accepts <c>struct</c> values, so this wraps <see cref="BackupProgress"/> (a
/// reference-type record) in a storable value type.
/// </summary>
public readonly record struct BackupProgressState(long BytesTransferred, long TotalBytes)
{
    public BackupProgress ToProgress() => new(BytesTransferred, TotalBytes);
}

/// <summary>
/// The single column driving every row of the backup progress display's three
/// synthetic tasks (scan/bar/stats), all hosted in one <see cref="AnsiConsole.Progress"/>
/// run, per design.md's "one unified column" decision: <c>Progress()</c> lays out every
/// configured column as one shared grid, and a column's assigned width is reserved on
/// every task-row even where it renders blank - verified empirically that using a
/// separate role-gated column per role would silently break the bar row's "dedicated,
/// width-sized line" requirement, since the scan/stats columns' widths would still be
/// reserved (blank) on the bar's row. Using a single column avoids this: with only one
/// column configured, it gets the full row width on every task, and each role's
/// returned <see cref="IRenderable"/> sizes itself to whatever width it is handed at
/// actual render time - the same self-sizing technique the former
/// <c>BackupProgressPanel</c> used, just returned per-role from one column instead of
/// yielding multiple lines from one multi-line renderable.
/// </summary>
public sealed class BackupProgressColumn(BackupProgressCalculator calculator) : ProgressColumn
{
    public const string RoleKey = "BackupProgressRole";
    public const string ProgressKey = "BackupProgressState";
    public const string OutcomeKey = "BackupProgressOutcome";

    private const string ScanLabel = " Scanning files...";
    private const string TransferLabel = " Backing up...";

    // Width of the fixed leading label column, sized to the longer of the two phases'
    // label text (per design.md's "both the label column and the trailing column use
    // fixed widths, sized to the longer of the two phases' content" decision) - shared
    // by both roles so the label column's width never depends on which phase is
    // currently rendering, which would otherwise leave the bar's own width dependent on
    // the label text's length even with a right-side placeholder alone.
    internal static readonly int LabelWidth = Math.Max(ScanLabel.Length, TransferLabel.Length);

    // Width of the fixed trailing column, matching the byte-based percent text's width -
    // constant regardless of the actual percentage, since FormatPercentText's numeric
    // field is itself fixed-width. Reused for the scan phase's blank placeholder so the
    // bar renders at the same length in both phases.
    internal static readonly int TrailingWidth = FormatPercentText(0).Length;

    // Composition, not inheritance - ProgressBarColumn is sealed. Reused for both the
    // scan role (indeterminate pulse) and the bar role (byte-based fill) per design.md's
    // "reuse _bar via a Grid, not Columns, dropping SpinnerColumn entirely" decision -
    // a spinner glyph is a couple of characters wide regardless of color, and only a
    // full-width bar gives the scan phase the same visual weight as the transfer phase
    // that follows it. CompletedStyle/FinishedStyle/IndeterminateStyle are all
    // recomputed per render call from the task's stamped BackupProgressOutcome (see
    // ApplyOutcomeStyle) rather than fixed here, since the bar's color is no longer
    // purely a function of Value/MaxValue.
    private readonly ProgressBarColumn _bar = new()
    {
        RemainingStyle = new Style(Color.Grey),
    };

    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime) =>
        task.State.Get<BackupProgressRole>(RoleKey) switch
        {
            BackupProgressRole.Scan => RenderScan(options, task, deltaTime),
            BackupProgressRole.Bar => RenderBar(options, task, deltaTime),
            BackupProgressRole.Stats => RenderStats(task),
            _ => new Text(string.Empty),
        };

    // Renders the scan phase's indeterminate indicator using the same shared _bar as
    // the transfer phase (IsIndeterminate is already set on the scan task by
    // BackupCommand), recolored to the neutral pastel color while running - or to the
    // matching outcome color if a hard error is stamped onto this task before it's
    // replaced by the bar/stats tasks, per the progress-reporting delta's "Progress bar
    // reflects run outcome severity" requirement. The trailing column renders blank
    // (not synthetic percentage-like text), since the scan phase has no percentage to
    // show - per design.md's "scan-phase placeholder is blank space, not synthetic
    // content" decision.
    private IRenderable RenderScan(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        ApplyOutcomeStyle(task);
        return RenderBarRow(options, task, deltaTime, ScanLabel, new string(' ', TrailingWidth));
    }

    // Delegates the bar's fill segments to Spectre's own ProgressBarColumn (composition,
    // not inheritance - ProgressBarColumn is sealed), reusing its native Unicode bar
    // glyph and fill/remaining rendering rather than hand-drawing ASCII characters.
    // While the run is still Running, the task's Value/MaxValue are driven from
    // BackupProgressCalculator's already-100%-on-zero-total percent, exactly as before.
    // Once a final outcome is stamped, the percent is forced to 100 instead - the bar's
    // final color reflects the run's actual outcome, not the byte-based percentage
    // reached at that moment (design.md's "Progress bar reflects run outcome severity"
    // decision - a failure doesn't always move the percentage to 100, and a
    // Move/Delete-only failure never touches it at all).
    private IRenderable RenderBar(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        var outcome = ApplyOutcomeStyle(task);

        var percent = outcome == BackupProgressOutcome.Running
            ? Math.Clamp(calculator.Calculate(task.State.Get<BackupProgressState>(ProgressKey).ToProgress()).PercentComplete, 0, 100)
            : 100;

        task.MaxValue = 100;
        task.Value = percent;

        return RenderBarRow(options, task, deltaTime, TransferLabel, FormatPercentText(percent));
    }

    // The percent text's numeric field is fixed-width (5 characters via "{0,5:0.0}"),
    // so this always returns the same length regardless of the value passed in - relied
    // upon by TrailingWidth to size the trailing column once, and by RenderScan's blank
    // placeholder to match that same width.
    private static string FormatPercentText(double percent) => $" {percent,5:0.0}%";

    // Combined with the label and trailing text via a three-column Grid rather than
    // Columns: Columns stacks a "greedy" full-width child (like the bar) and a second
    // child onto separate rows instead of the same line (verified empirically), whereas
    // Grid's explicit fixed column widths place them side by side on one line, per the
    // progress-reporting delta's "dedicated, width-sized line" requirement. Shared by
    // both RenderScan (label = the scan label, trailing = a blank placeholder) and
    // RenderBar (label = the transfer label, trailing = the percent text), since both
    // roles now render the same shared _bar. LabelWidth and TrailingWidth are both
    // fixed constants shared across roles (not sized to whichever text this particular
    // call passes in), which is what actually guarantees the bar itself renders at the
    // same width in both phases.
    private IRenderable RenderBarRow(RenderOptions options, ProgressTask task, TimeSpan deltaTime, string labelText, string trailingText)
    {
        var availableWidth = options.ConsoleSize.Width;
        var barWidth = Math.Max(1, availableWidth - LabelWidth - TrailingWidth);
        _bar.Width = barWidth;

        var grid = new Grid()
            .AddColumn(new GridColumn { Width = LabelWidth, NoWrap = true, Padding = new Padding(0) })
            .AddColumn(new GridColumn { Width = barWidth, NoWrap = true, Padding = new Padding(0) })
            .AddColumn(new GridColumn { Width = TrailingWidth, NoWrap = true, Padding = new Padding(0) });
        grid.AddRow(new Text(labelText.PadRight(LabelWidth)), _bar.Render(options, task, deltaTime), new Text(trailingText.PadRight(TrailingWidth)));
        return grid;
    }

    // Sets _bar's fill/pulse styles for the task's stamped BackupProgressOutcome
    // (defaulting to Running when unset), per design.md's "one outcome enum ... read by
    // both scan and bar rendering" decision. Reuses OutcomeStyle's existing Color
    // fields rather than re-declaring the same pastel constants, so this and the
    // completion-report coloring can never drift apart. Returns the resolved outcome so
    // RenderBar (which also needs it for the percent-vs-fixed-100 choice) doesn't have
    // to read task.State a second time.
    private BackupProgressOutcome ApplyOutcomeStyle(ProgressTask task)
    {
        var outcome = task.State.Get<BackupProgressOutcome>(OutcomeKey);
        var color = outcome switch
        {
            BackupProgressOutcome.Success => OutcomeStyle.Success.Foreground,
            BackupProgressOutcome.PartialFailure => OutcomeStyle.PartialFailure.Foreground,
            BackupProgressOutcome.Error => OutcomeStyle.Error.Foreground,
            _ => OutcomeStyle.PartialFailure.Foreground, // Running: amber, distinct from every final outcome color.
        };

        var style = new Style(color);
        _bar.CompletedStyle = style;
        _bar.FinishedStyle = style;
        _bar.IndeterminateStyle = outcome == BackupProgressOutcome.Running
            ? new Style(OutcomeStyle.Neutral.Foreground)
            : style;

        return outcome;
    }

    private Grid RenderStats(ProgressTask task)
    {
        var snapshot = calculator.Calculate(task.State.Get<BackupProgressState>(ProgressKey).ToProgress());
        return BuildStatsRow(snapshot);
    }

    private static Grid BuildStatsRow(ProgressSnapshot snapshot)
    {
        var transferred = $"{BackupRunSummaryFormatter.FormatBytes(snapshot.BytesTransferred)} / {BackupRunSummaryFormatter.FormatBytes(snapshot.TotalBytes)}";
        var throughput = $"{BackupRunSummaryFormatter.FormatBytes((long)snapshot.ThroughputBytesPerSecond)}/s";
        var eta = snapshot.EstimatedTimeRemaining is { } remaining
            ? $"ETA {BackupRunSummaryFormatter.FormatDuration(remaining)}"
            : "ETA calculating...";

        // Widths are generous upper bounds for each field's longest realistic rendering
        // (e.g. "999.99 GB / 999.99 GB"), so normal content never needs to overflow or
        // wrap - the fixed Width, not the content, is what determines each column's
        // horizontal footprint.
        var stats = new Grid()
            .AddColumn(new GridColumn { Width = 26, NoWrap = true })
            .AddColumn(new GridColumn { Width = 14, NoWrap = true })
            .AddColumn(new GridColumn { Width = 20, NoWrap = true });
        stats.AddRow(new Text(transferred), new Text(throughput), new Text(eta));
        return stats;
    }
}
