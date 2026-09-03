using Spectre.Console.Testing;
using Vara.Cli.Presentation;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class DeletedReportPresenterTests
{
    [Fact]
    public void Empty_list_shows_plain_message_not_an_empty_table()
    {
        var console = new TestConsole();

        DeletedReportPresenter.Render(console, []);

        Assert.Contains("No deleted files recorded.", console.Output);
    }

    [Fact]
    public void Renders_an_aligned_table_with_headers_and_a_rounded_border()
    {
        var console = new TestConsole();
        var deleted = new[]
        {
            new FileVersionRecord(
                1,
                1,
                "docs\\old.txt",
                null,
                "abc123",
                2048,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                FileChangeKind.Deleted,
                new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero)),
        };

        DeletedReportPresenter.Render(console, deleted);

        Assert.Contains("Path", console.Output);
        Assert.Contains("Deleted At", console.Output);
        Assert.Contains("docs\\old.txt", console.Output);
        Assert.Contains("2 KB", console.Output);
        Assert.Contains('╭', console.Output);
    }

    [Fact]
    public void Rows_are_colored_in_the_deleted_style()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;
        var deleted = new[]
        {
            new FileVersionRecord(1, 1, "a.txt", null, "hash", 10, DateTimeOffset.UnixEpoch, FileChangeKind.Deleted, DateTimeOffset.UnixEpoch),
        };

        DeletedReportPresenter.Render(console, deleted);

        Assert.Contains("\u001b[38;5;131m", console.Output); // IndianRed
    }
}
