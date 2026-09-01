using Spectre.Console;
using Vara.Application.Reporting;
using Vara.Core.Snapshots;

namespace Vara.Cli.Presentation;

/// <summary>
/// Renders the `snapshots` command's listing as an aligned table with column headers,
/// per the `snapshot-history` delta's "List snapshots" requirement. The Status column
/// is colored per the snapshot's outcome severity (reusing `OutcomeStyle`), numeric
/// columns are right-aligned, and the table uses the shared rounded-corner border
/// convention - all per the `cli-presentation`/`snapshot-history` deltas' coloring and
/// alignment requirements.
/// </summary>
public static class SnapshotsTablePresenter
{
    public static void Render(IAnsiConsole console, IReadOnlyList<Snapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            console.WriteLine("No snapshots recorded yet.");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("#");
        table.AddColumn("Started");
        table.AddColumn("Status");
        table.AddColumn(new TableColumn("Added").RightAligned());
        table.AddColumn(new TableColumn("Changed").RightAligned());
        table.AddColumn(new TableColumn("Moved").RightAligned());
        table.AddColumn(new TableColumn("Deleted").RightAligned());
        table.AddColumn(new TableColumn("Transferred").RightAligned());

        foreach (var snapshot in snapshots)
        {
            table.AddRow(
                new Text(snapshot.Id.ToString()),
                new Text(snapshot.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")),
                new Text(snapshot.Status.ToString(), StatusStyle(snapshot.Status)),
                new Text(snapshot.Stats.FilesAdded.ToString()),
                new Text(snapshot.Stats.FilesChanged.ToString()),
                new Text(snapshot.Stats.FilesMoved.ToString()),
                new Text(snapshot.Stats.FilesDeleted.ToString()),
                new Text(BackupRunSummaryFormatter.FormatBytes(snapshot.Stats.BytesTransferred)));
        }

        console.Write(table);
    }

    private static Style StatusStyle(SnapshotStatus status) => status switch
    {
        SnapshotStatus.Complete => OutcomeStyle.Success,
        SnapshotStatus.Failed => OutcomeStyle.Error,
        _ => OutcomeStyle.Neutral,
    };
}
