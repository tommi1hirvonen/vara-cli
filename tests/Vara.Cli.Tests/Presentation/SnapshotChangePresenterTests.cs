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

    [Theory]
    [InlineData(FileChangeKind.Added, "\u001b[38;5;121m")]
    [InlineData(FileChangeKind.Changed, "\u001b[38;5;186m")]
    [InlineData(FileChangeKind.Moved, "\u001b[38;5;153m")]
    [InlineData(FileChangeKind.Deleted, "\u001b[38;5;131m")]
    [InlineData(FileChangeKind.Linked, "\u001b[38;5;183m")]
    public void Renders_each_directory_rollup_count_in_its_change_kind_color(FileChangeKind kind, string expectedEscapeCode)
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;
        var now = DateTimeOffset.UnixEpoch;
        var file = new FileVersionRecord(1, 7, "file.txt", null, "hash", 1, now, kind, now);
        var directory = new SnapshotChangeDirectory(
            string.Empty,
            kind == FileChangeKind.Added ? 1 : 0,
            kind == FileChangeKind.Changed ? 1 : 0,
            kind == FileChangeKind.Moved ? 1 : 0,
            kind == FileChangeKind.Deleted ? 1 : 0,
            kind == FileChangeKind.Linked ? 1 : 0,
            [file]);
        var report = new SnapshotChangeReport(
            new Snapshot(7, now, now, SnapshotStatus.Complete, SnapshotStats.Empty),
            ".",
            [directory]);

        SnapshotChangePresenter.Render(console, report, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains(expectedEscapeCode, console.Output);
        Assert.Contains(kind switch
        {
            FileChangeKind.Added => "+1",
            FileChangeKind.Changed => "~1",
            FileChangeKind.Moved => "->1",
            FileChangeKind.Deleted => "-1",
            FileChangeKind.Linked => "1",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }, console.Output);
    }

    [Fact]
    public void Limits_tree_depth_and_counts_hidden_descendant_directories_per_boundary()
    {
        var console = new TestConsole();
        var snapshot = new Snapshot(7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SnapshotStatus.Complete, SnapshotStats.Empty);
        var report = new SnapshotChangeReport(snapshot, ".",
        [
            new SnapshotChangeDirectory("", 4, 0, 0, 0, 0, []),
            new SnapshotChangeDirectory(@"alpha", 2, 0, 0, 0, 0, []),
            new SnapshotChangeDirectory(@"alpha\deep", 2, 0, 0, 0, 0, []),
            new SnapshotChangeDirectory(@"alpha\deep\deeper", 1, 0, 0, 0, 0, []),
            new SnapshotChangeDirectory(@"beta", 2, 0, 0, 0, 0, []),
            new SnapshotChangeDirectory(@"beta\deep", 1, 0, 0, 0, 0, []),
        ]);

        SnapshotChangePresenter.Render(console, report, new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth: 1);

        Assert.Contains("alpha", console.Output);
        Assert.Contains("beta", console.Output);
        Assert.DoesNotContain("deeper (+", console.Output);
        Assert.Contains("... 2 deeper directories not shown", console.Output);
        Assert.Contains("... 1 deeper directory not shown", console.Output);
        Assert.Contains("+4", console.Output);
        Assert.Contains("+2", console.Output);
    }

    [Fact]
    public void Renders_markup_like_directory_names_literally()
    {
        var console = new TestConsole();
        var report = new SnapshotChangeReport(
            new Snapshot(7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SnapshotStatus.Complete, SnapshotStats.Empty),
            "[red]",
            [new SnapshotChangeDirectory("[red]", 1, 0, 0, 0, 0, [])]);

        SnapshotChangePresenter.Render(console, report, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains("[red]", console.Output);
    }

    [Fact]
    public void Depth_zero_shows_only_scope_root_and_reports_all_omitted_directory_descendants()
    {
        var console = new TestConsole();
        var report = new SnapshotChangeReport(
            new Snapshot(7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SnapshotStatus.Complete, SnapshotStats.Empty),
            @"src",
            [
                new SnapshotChangeDirectory(@"src", 3, 0, 0, 0, 0, []),
                new SnapshotChangeDirectory(@"src\deep", 2, 0, 0, 0, 0, []),
                new SnapshotChangeDirectory(@"src\deep\deeper", 1, 0, 0, 0, 0, []),
            ]);

        SnapshotChangePresenter.Render(console, report, new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth: 0);

        Assert.Contains("src", console.Output);
        Assert.DoesNotContain("deep (+", console.Output);
        Assert.DoesNotContain("deeper (+", console.Output);
        Assert.Contains("... 2 deeper directories not shown", console.Output);
        Assert.Contains("+3", console.Output);
    }

    [Fact]
    public void Unlimited_depth_preserves_full_tree_and_does_not_show_omission_markers()
    {
        var console = new TestConsole();
        var report = new SnapshotChangeReport(
            new Snapshot(7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SnapshotStatus.Complete, SnapshotStats.Empty),
            ".",
            [
                new SnapshotChangeDirectory("", 1, 0, 0, 0, 0, []),
                new SnapshotChangeDirectory(@"alpha", 1, 0, 0, 0, 0, []),
                new SnapshotChangeDirectory(@"alpha\deep", 1, 0, 0, 0, 0, []),
            ]);

        SnapshotChangePresenter.Render(console, report, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains("deep", console.Output);
        Assert.DoesNotContain("not shown", console.Output);
    }

    [Fact]
    public void Depth_limit_does_not_add_an_omission_marker_when_all_directories_are_visible()
    {
        var console = new TestConsole();
        var report = new SnapshotChangeReport(
            new Snapshot(7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SnapshotStatus.Complete, SnapshotStats.Empty),
            ".",
            [
                new SnapshotChangeDirectory("", 1, 0, 0, 0, 0, []),
                new SnapshotChangeDirectory(@"alpha", 1, 0, 0, 0, 0, []),
            ]);

        SnapshotChangePresenter.Render(console, report, new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth: 1);

        Assert.Contains("alpha", console.Output);
        Assert.DoesNotContain("not shown", console.Output);
    }
}
