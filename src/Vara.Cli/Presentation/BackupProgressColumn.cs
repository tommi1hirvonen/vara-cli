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

    private readonly SpinnerColumn _spinner = new() { Style = new Style(Color.LightSkyBlue1) };

    // Composition, not inheritance - ProgressBarColumn is sealed. CompletedStyle
    // covers the fill while the task is in progress; FinishedStyle is what
    // ProgressBarColumn switches to once the task reaches 100% (Spectre defaults this
    // to a plain Color.Green independent of CompletedStyle, so it must be set
    // explicitly too or a completed bar would revert to the non-pastel default green).
    private readonly ProgressBarColumn _bar = new()
    {
        CompletedStyle = new Style(Color.PaleGreen1),
        FinishedStyle = new Style(Color.PaleGreen1),
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

    // Delegates the spinner glyph itself to Spectre's own SpinnerColumn (composition,
    // not inheritance - SpinnerColumn is sealed) so its frame-advancing and Unicode
    // capability fallback are reused rather than reimplemented, per the
    // progress-reporting delta's "Scan-phase progress indication" requirement.
    private IRenderable RenderScan(RenderOptions options, ProgressTask task, TimeSpan deltaTime) =>
        new Columns(_spinner.Render(options, task, deltaTime), new Text(" Scanning files...", new Style(Color.LightSkyBlue1))).Collapse();

    // Delegates the bar's fill segments to Spectre's own ProgressBarColumn (composition,
    // not inheritance - ProgressBarColumn is sealed), reusing its native Unicode bar
    // glyph and fill/remaining rendering rather than hand-drawing ASCII characters.
    // The task's Value/MaxValue are driven here (not from raw admitted bytes) by
    // BackupProgressCalculator's already-100%-on-zero-total percent - ProgressBarColumn's
    // own fill ratio does not special-case a zero MaxValue the way ProgressTask.Percentage
    // does (verified empirically: it renders fully unfilled at 0/0, which would
    // visually contradict the percent text otherwise correctly showing 100%) - so
    // driving both the fill and the percent text from the same 0-100 percent value
    // keeps them always consistent. Combined with the percent text via a two-column
    // Grid rather than Columns: Columns stacks a "greedy" full-width child (like the
    // bar) and a second child onto separate rows instead of the same line (verified
    // empirically), whereas Grid's explicit fixed column widths place them side by
    // side on one line, per the progress-reporting delta's "dedicated, width-sized
    // line" requirement.
    private IRenderable RenderBar(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        var snapshot = calculator.Calculate(task.State.Get<BackupProgressState>(ProgressKey).ToProgress());
        var percent = Math.Clamp(snapshot.PercentComplete, 0, 100);
        var percentText = $" {percent,5:0.0}%";

        task.MaxValue = 100;
        task.Value = percent;

        var availableWidth = options.ConsoleSize.Width;
        var barWidth = Math.Max(1, availableWidth - percentText.Length);
        _bar.Width = barWidth;

        var grid = new Grid()
            .AddColumn(new GridColumn { Width = barWidth, NoWrap = true, Padding = new Padding(0) })
            .AddColumn(new GridColumn { Width = percentText.Length, NoWrap = true, Padding = new Padding(0) });
        grid.AddRow(_bar.Render(options, task, deltaTime), new Text(percentText));
        return grid;
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
