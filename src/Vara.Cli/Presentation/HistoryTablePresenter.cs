using Spectre.Console;
using Vara.Application.Reporting;
using Vara.Core.Snapshots;

namespace Vara.Cli.Presentation;

/// <summary>
/// Renders the `history` command's listing as an aligned table with column headers,
/// per the `snapshot-history` delta's "File version history" requirement.
/// </summary>
public static class HistoryTablePresenter
{
    public static void Render(IAnsiConsole console, IReadOnlyList<FileVersionRecord> versions)
    {
        var table = new Table();
        table.AddColumn("#");
        table.AddColumn("Recorded");
        table.AddColumn("Change");
        table.AddColumn("Size");

        foreach (var version in versions)
        {
            var change = version.ChangeKind == FileChangeKind.Deleted
                ? $"{version.ChangeKind} (deleted)"
                : version.ChangeKind.ToString();

            table.AddRow(
                version.Id.ToString(),
                version.RecordedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                change,
                BackupRunSummaryFormatter.FormatBytes(version.Size));
        }

        console.Write(table);
    }
}
