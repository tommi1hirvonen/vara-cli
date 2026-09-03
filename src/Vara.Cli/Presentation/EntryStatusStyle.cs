using Spectre.Console;
using Vara.Application.History;

namespace Vara.Cli.Presentation;

/// <summary>
/// The styles used to mark a <see cref="DirectoryEntry"/>'s or deleted-report row's status,
/// shared by <see cref="DirectoryListingPresenter"/> and <see cref="DeletedReportPresenter"/>
/// so the "deleted"/"moved" coloring stays consistent with <see cref="HistoryTablePresenter"/>'s
/// own change-kind palette (plain pastel colors, not the severity palette in
/// <see cref="OutcomeStyle"/> - this is a kind/status axis, not a severity axis).
/// </summary>
public static class EntryStatusStyle
{
    public static readonly Style Live = Style.Plain;
    public static readonly Style Deleted = new(Color.IndianRed);
    public static readonly Style Moved = new(Color.LightSkyBlue1);

    public static Style For(DirectoryEntryStatus status) => status switch
    {
        DirectoryEntryStatus.Deleted => Deleted,
        DirectoryEntryStatus.Moved => Moved,
        _ => Live,
    };
}
