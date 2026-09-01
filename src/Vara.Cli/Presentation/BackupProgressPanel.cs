using Spectre.Console;
using Spectre.Console.Rendering;
using Vara.Application.Backup;
using Vara.Application.Reporting;

namespace Vara.Cli.Presentation;

/// <summary>
/// The live backup-progress display: a progress bar on its own line and a fixed-column
/// stats line below it (bytes transferred/total, throughput, ETA), per the
/// progress-reporting delta's "Progress bar occupies a dedicated, width-sized line" and
/// "Numeric progress fields do not shift layout as values change" requirements. The
/// stats line is a borderless <see cref="Grid"/> with explicit column widths, so a
/// value's digit count changing never shifts a neighboring field's horizontal position.
/// The bar itself is hand-drawn with plain ASCII fill characters (not Spectre's
/// <c>ProgressBar</c>, which turned out to be an internal type not part of its public
/// API) - ASCII also sidesteps any code-page/font concerns on a legacy Windows conhost
/// that Unicode block characters could otherwise raise.
/// </summary>
/// <remarks>
/// Recomputes its displayed values fresh on every <see cref="Render"/> call (rather than
/// caching a snapshot), so a heartbeat-driven redraw - calling
/// <c>LiveDisplayContext.Refresh()</c> with no new byte-progress event - still advances
/// elapsed-time-based throughput/ETA. This is what lets a plain periodic timer replace
/// <c>ProgressHeartbeat</c>'s snapshot-replay role (design.md's "keep the monotonic
/// guard, drop the custom timer" decision): <see cref="LiveDisplay"/> has no built-in
/// auto-refresh of its own (confirmed by spike, unlike <c>Progress()</c>'s
/// <c>AutoRefresh</c>), so something still has to call <c>Refresh()</c> periodically,
/// but that something no longer needs to remember or replay a progress value itself.
/// </remarks>
public sealed class BackupProgressPanel(BackupProgressCalculator calculator) : IRenderable
{
    private BackupProgress _current = new(0, 0);

    /// <summary>Records the most recently admitted progress report to be reflected by the next render.</summary>
    public void Update(BackupProgress progress) => _current = progress;

    public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(maxWidth, 20), maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var snapshot = calculator.Calculate(_current);

        foreach (var segment in RenderBar(snapshot.PercentComplete, maxWidth))
        {
            yield return segment;
        }

        yield return Segment.LineBreak;

        IRenderable stats = BuildStatsRow(snapshot);
        foreach (var segment in stats.Render(options, maxWidth))
        {
            yield return segment;
        }
    }

    private static IEnumerable<Segment> RenderBar(double percentComplete, int maxWidth)
    {
        var percentText = $" {Math.Clamp(percentComplete, 0, 100),5:0.0}%";
        var barWidth = Math.Max(1, maxWidth - percentText.Length);
        var filled = Math.Clamp((int)Math.Round(barWidth * Math.Clamp(percentComplete, 0, 100) / 100.0), 0, barWidth);

        if (filled > 0)
        {
            yield return new Segment(new string('#', filled), new Style(Color.Green));
        }

        if (barWidth - filled > 0)
        {
            yield return new Segment(new string('-', barWidth - filled), new Style(Color.Grey));
        }

        yield return new Segment(percentText);
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

