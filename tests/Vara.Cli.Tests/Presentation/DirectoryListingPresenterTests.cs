using Spectre.Console.Testing;
using Vara.Application.History;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class DirectoryListingPresenterTests
{
    [Fact]
    public void Empty_list_shows_plain_message_not_an_empty_table()
    {
        var console = new TestConsole();

        DirectoryListingPresenter.Render(console, []);

        Assert.Contains("No entries recorded in this directory.", console.Output);
    }

    [Fact]
    public void Renders_an_aligned_table_with_headers_and_a_rounded_border()
    {
        var console = new TestConsole();
        var entries = new[]
        {
            new DirectoryEntry("main.py", DirectoryEntryKind.File, DirectoryEntryStatus.Live, 2048),
            new DirectoryEntry("utils", DirectoryEntryKind.Directory, DirectoryEntryStatus.Live, null),
        };

        DirectoryListingPresenter.Render(console, entries);

        Assert.Contains("Name", console.Output);
        Assert.Contains("Kind", console.Output);
        Assert.Contains("Status", console.Output);
        Assert.Contains("main.py", console.Output);
        Assert.Contains("2 KB", console.Output);
        Assert.Contains('╭', console.Output);
    }

    [Fact]
    public void Deleted_and_live_entries_are_interleaved_alphabetically_not_grouped()
    {
        var console = new TestConsole();
        var entries = new[]
        {
            new DirectoryEntry("b_live.txt", DirectoryEntryKind.File, DirectoryEntryStatus.Live, 10),
            new DirectoryEntry("a_deleted.txt", DirectoryEntryKind.File, DirectoryEntryStatus.Deleted, 5),
        };

        DirectoryListingPresenter.Render(console, entries);

        var deletedIndex = console.Output.IndexOf("a_deleted.txt", StringComparison.Ordinal);
        var liveIndex = console.Output.IndexOf("b_live.txt", StringComparison.Ordinal);
        Assert.True(deletedIndex < liveIndex, "expected the alphabetically-earlier deleted entry to render before the live one");
    }

    [Fact]
    public void A_moved_entry_shows_its_destination()
    {
        var console = new TestConsole();
        var entries = new[]
        {
            new DirectoryEntry("a.txt", DirectoryEntryKind.File, DirectoryEntryStatus.Moved, 10, MovedTo: "archive\\a.txt"),
        };

        DirectoryListingPresenter.Render(console, entries);

        Assert.Contains("Moved", console.Output);
        Assert.Contains("archive\\a.txt", console.Output);
    }

    [Theory]
    [InlineData(DirectoryEntryStatus.Deleted, "\u001b[38;5;131m")] // IndianRed
    [InlineData(DirectoryEntryStatus.Moved, "\u001b[38;5;153m")] // LightSkyBlue1
    public void Deleted_and_moved_rows_are_colored_distinctly(DirectoryEntryStatus status, string expectedEscapeCode)
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;
        var entries = new[] { new DirectoryEntry("a.txt", DirectoryEntryKind.File, status, 10, status == DirectoryEntryStatus.Moved ? "b.txt" : null) };

        DirectoryListingPresenter.Render(console, entries);

        Assert.Contains(expectedEscapeCode, console.Output);
    }
}
