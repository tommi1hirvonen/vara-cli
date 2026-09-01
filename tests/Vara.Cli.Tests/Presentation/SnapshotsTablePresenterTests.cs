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
    public void Non_empty_list_renders_an_aligned_table_with_headers()
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
    }
}
