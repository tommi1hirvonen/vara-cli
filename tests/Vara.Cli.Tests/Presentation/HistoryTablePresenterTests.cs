using Spectre.Console.Testing;
using Vara.Cli.Presentation;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class HistoryTablePresenterTests
{
    [Fact]
    public void Empty_list_shows_plain_message_not_an_empty_table()
    {
        var console = new TestConsole();

        HistoryTablePresenter.Render(console, []);

        Assert.Contains("No version history recorded for this path.", console.Output);
    }

    [Fact]
    public void Renders_an_aligned_table_with_headers_and_a_rounded_border()
    {
        var console = new TestConsole();
        var versions = new[]
        {
            new FileVersionRecord(
                1,
                1,
                "docs\\file.txt",
                null,
                "abc123",
                2048,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                FileChangeKind.Added,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new FileVersionRecord(
                2,
                2,
                "docs\\file.txt",
                null,
                "abc123",
                2048,
                new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero),
                FileChangeKind.Deleted,
                new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero)),
        };

        HistoryTablePresenter.Render(console, versions);

        Assert.Contains("Recorded", console.Output);
        Assert.Contains("Change", console.Output);
        Assert.Contains("Added", console.Output);
        Assert.Contains("Deleted", console.Output);
        Assert.Contains("2 KB", console.Output);
        Assert.Contains('╭', console.Output); // rounded border corner
    }

    [Theory]
    [InlineData(FileChangeKind.Added, "\u001b[38;5;121m")] // PaleGreen1
    [InlineData(FileChangeKind.Changed, "\u001b[38;5;186m")] // LightGoldenrod2
    [InlineData(FileChangeKind.Moved, "\u001b[38;5;153m")] // LightSkyBlue1
    [InlineData(FileChangeKind.Deleted, "\u001b[38;5;131m")] // IndianRed
    public void Change_cell_is_colored_per_kind(FileChangeKind kind, string expectedEscapeCode)
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;
        var versions = new[]
        {
            new FileVersionRecord(1, 1, "docs\\file.txt", null, "abc123", 2048, DateTimeOffset.UnixEpoch, kind, DateTimeOffset.UnixEpoch),
        };

        HistoryTablePresenter.Render(console, versions);

        Assert.Contains(expectedEscapeCode, console.Output);
    }
}
