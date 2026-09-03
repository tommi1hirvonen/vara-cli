using Spectre.Console;
using Vara.Application.Reporting;
using Vara.Core.Snapshots;

namespace Vara.Cli.Presentation;

/// <summary>
/// Renders the `deleted` command's recently-deleted report as an aligned table with column
/// headers, most recently deleted first, per the `backup-browsing` capability's
/// recently-deleted report requirement. Every row is a deletion, so all rows use
/// <see cref="EntryStatusStyle.Deleted"/> uniformly.
/// </summary>
public static class DeletedReportPresenter
{
    public static void Render(IAnsiConsole console, IReadOnlyList<FileVersionRecord> deleted)
    {
        if (deleted.Count == 0)
        {
            console.WriteLine("No deleted files recorded.");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Path");
        table.AddColumn("Deleted At");
        table.AddColumn(new TableColumn("Size").RightAligned());

        foreach (var record in deleted)
        {
            table.AddRow(
                new Text(record.RelativePath, EntryStatusStyle.Deleted),
                new Text(record.RecordedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"), EntryStatusStyle.Deleted),
                new Text(BackupRunSummaryFormatter.FormatBytes(record.Size), EntryStatusStyle.Deleted));
        }

        console.Write(table);
    }
}
