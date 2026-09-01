using Spectre.Console;
using Vara.Application.Reporting;
using Vara.Core.Snapshots;

namespace Vara.Cli.Presentation;

/// <summary>
/// Renders the `snapshots` command's listing as an aligned table with column headers,
/// per the `snapshot-history` delta's "List snapshots" requirement.
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

        var table = new Table();
        table.AddColumn("#");
        table.AddColumn("Started");
        table.AddColumn("Status");
        table.AddColumn("Added");
        table.AddColumn("Changed");
        table.AddColumn("Moved");
        table.AddColumn("Deleted");
        table.AddColumn("Transferred");

        foreach (var snapshot in snapshots)
        {
            table.AddRow(
                snapshot.Id.ToString(),
                snapshot.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                snapshot.Status.ToString(),
                snapshot.Stats.FilesAdded.ToString(),
                snapshot.Stats.FilesChanged.ToString(),
                snapshot.Stats.FilesMoved.ToString(),
                snapshot.Stats.FilesDeleted.ToString(),
                BackupRunSummaryFormatter.FormatBytes(snapshot.Stats.BytesTransferred));
        }

        console.Write(table);
    }
}
