using Spectre.Console;
using Vara.Application.Reporting;
using Vara.Core.Snapshots;

namespace Vara.Cli.Presentation;

/// <summary>
/// Renders the `history` command's listing as an aligned table with column headers,
/// per the `snapshot-history` delta's "File version history" requirement. The Change
/// column is colored per its kind, the Size column is right-aligned, and the table
/// uses the shared rounded-corner border convention, matching `SnapshotsTablePresenter`'s
/// existing empty-state behavior when no version history remains for a path.
/// </summary>
public static class HistoryTablePresenter
{
    public static void Render(IAnsiConsole console, IReadOnlyList<FileVersionRecord> versions)
    {
        if (versions.Count == 0)
        {
            console.WriteLine("No version history recorded for this path.");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("#");
        table.AddColumn("Recorded");
        table.AddColumn("Change");
        table.AddColumn(new TableColumn("Size").RightAligned());

        foreach (var version in versions)
        {
            var change = version.ChangeKind == FileChangeKind.Deleted
                ? $"{version.ChangeKind} (deleted)"
                : version.ChangeKind.ToString();

            table.AddRow(
                new Text(version.Id.ToString()),
                new Text(version.RecordedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")),
                new Text(change, ChangeKindStyle(version.ChangeKind)),
                new Text(BackupRunSummaryFormatter.FormatBytes(version.Size)));
        }

        console.Write(table);
    }

    // Distinct from the severity styles (OutcomeStyle) - "added/changed/moved/deleted"
    // is a kind axis, not a severity axis, per design.md's "Status/change-kind
    // coloring" decision, so these are plain (non-bold) pastel colors of their own.
    private static Style ChangeKindStyle(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => new Style(Color.PaleGreen1),
        FileChangeKind.Changed => new Style(Color.LightGoldenrod2),
        FileChangeKind.Moved => new Style(Color.LightSkyBlue1),
        FileChangeKind.Deleted => new Style(Color.IndianRed),
        FileChangeKind.Linked => new Style(Color.Plum2),
        _ => Style.Plain,
    };
}
