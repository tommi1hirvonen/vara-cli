using Spectre.Console;
using Vara.Application.History;
using Vara.Application.Reporting;

namespace Vara.Cli.Presentation;

/// <summary>
/// Renders the `browse` command's one-level directory listing as an aligned table with
/// column headers, per the `backup-browsing` capability's directory-listing requirements.
/// Deleted/moved entries are interleaved with live ones (not grouped into a separate
/// section) and colored via <see cref="EntryStatusStyle"/>; the Size column is right-aligned
/// and blank for directories.
/// </summary>
public static class DirectoryListingPresenter
{
    public static void Render(IAnsiConsole console, IReadOnlyList<DirectoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            console.WriteLine("No entries recorded in this directory.");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Kind");
        table.AddColumn("Status");
        table.AddColumn(new TableColumn("Size").RightAligned());

        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var style = EntryStatusStyle.For(entry.Status);
            var statusText = entry.Status switch
            {
                DirectoryEntryStatus.Deleted => "Deleted",
                DirectoryEntryStatus.Moved => $"Moved -> {entry.MovedTo}",
                _ => "Live",
            };

            table.AddRow(
                new Text(entry.Name, style),
                new Text(entry.Kind.ToString(), style),
                new Text(statusText, style),
                new Text(entry.Size is { } size ? BackupRunSummaryFormatter.FormatBytes(size) : "-", style));
        }

        console.Write(table);
    }
}
