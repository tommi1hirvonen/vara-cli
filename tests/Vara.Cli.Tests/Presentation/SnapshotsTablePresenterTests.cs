using Spectre.Console.Testing;
using Vara.Cli.Presentation;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class SnapshotsTablePresenterTests
{
    [Fact]
    public void Empty_list_shows_plain_message_not_an_empty_table()
    {
        var console = new TestConsole();

        SnapshotsTablePresenter.Render(console, []);

        Assert.Contains("No snapshots recorded yet.", console.Output);
    }

    [Fact]
    public void Non_empty_list_renders_an_aligned_table_with_headers_and_a_rounded_border()
    {
        var console = new TestConsole();
        var snapshots = new[]
        {
            new Snapshot(
                1,
                new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2024, 1, 1, 12, 5, 0, TimeSpan.Zero),
                SnapshotStatus.Complete,
                new SnapshotStats(1024, 3, 1, 0, 0, 0)),
        };

        SnapshotsTablePresenter.Render(console, snapshots);

        Assert.Contains("Started", console.Output);
        Assert.Contains("Transferred", console.Output);
        Assert.Contains("Complete", console.Output);
        Assert.Contains("1 KB", console.Output);
        Assert.Contains('╭', console.Output); // rounded border corner
    }

    [Theory]
    [InlineData(SnapshotStatus.Complete, "\u001b[1;38;5;121m")] // bold PaleGreen1
    [InlineData(SnapshotStatus.Failed, "\u001b[1;38;5;131m")] // bold IndianRed
    [InlineData(SnapshotStatus.Running, "\u001b[1;38;5;153m")] // bold LightSkyBlue1
    [InlineData(SnapshotStatus.Cancelled, "\u001b[1;38;5;186m")] // bold LightGoldenrod2 (same warning style as a partial failure)
    public void Status_cell_is_colored_per_outcome_severity(SnapshotStatus status, string expectedEscapeCode)
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;
        var snapshots = new[]
        {
            new Snapshot(1, DateTimeOffset.UnixEpoch, null, status, SnapshotStats.Empty),
        };

        SnapshotsTablePresenter.Render(console, snapshots);

        Assert.Contains(expectedEscapeCode, console.Output);
    }
}
