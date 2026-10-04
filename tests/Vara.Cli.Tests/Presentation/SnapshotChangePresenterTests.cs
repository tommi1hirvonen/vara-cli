using Spectre.Console.Testing;
using Vara.Application.History;
using Vara.Cli.Presentation;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class SnapshotChangePresenterTests
{
    [Fact]
    public void Renders_sparse_directory_rollups_and_configured_source_labels_without_file_paths()
    {
        var console = new TestConsole();
        var snapshot = new Snapshot(7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SnapshotStatus.Complete,
            new SnapshotStats(1024, 1, 1, 1, 1, 0));
        var report = new SnapshotChangeReport(snapshot, ".",
        [
            new SnapshotChangeDirectory("", 1, 1, 1, 1, 1, []),
            new SnapshotChangeDirectory(@"src", 1, 1, 1, 1, 1, []),
            new SnapshotChangeDirectory(@"src\nested", 0, 1, 1, 0, 0, []),
        ]);

        SnapshotChangePresenter.Render(console, report, new HashSet<string>([@"src"], StringComparer.OrdinalIgnoreCase));

        Assert.Contains("Snapshot #7", console.Output);
        Assert.Contains("src [source]", console.Output);
        Assert.Contains("nested", console.Output);
        Assert.Contains("+1 ~1 ->1 -1 link 1", console.Output);
        Assert.DoesNotContain("file.txt", console.Output);
    }

    [Fact]
    public void Renders_requested_file_details_and_move_origins_as_literal_text()
    {
        var console = new TestConsole();
        var now = DateTimeOffset.UnixEpoch;
        var moved = new FileVersionRecord(1, 7, @"src\[red]file.txt", @"old\file.txt", "hash", 10, now,
            FileChangeKind.Moved, now);
        var snapshot = new Snapshot(7, now, now, SnapshotStatus.Complete, SnapshotStats.Empty);
        var report = new SnapshotChangeReport(snapshot, @"src",
        [
            new SnapshotChangeDirectory(@"src", 1, 0, 1, 0, 0, [moved]),
            new SnapshotChangeDirectory(@"src\nested", 1, 0, 0, 0, 0, []),
        ]);

        SnapshotChangePresenter.Render(console, report, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains("[red]file.txt", console.Output);
        Assert.Contains("Moved from old\\file.txt", console.Output);
    }
}
