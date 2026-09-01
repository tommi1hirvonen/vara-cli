using Spectre.Console.Testing;
using Vara.Cli.Presentation;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class HistoryTablePresenterTests
{
    [Fact]
    public void Renders_an_aligned_table_with_headers()
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
    }
}
