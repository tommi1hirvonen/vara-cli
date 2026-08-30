using Vara.Application.Backup;
using Vara.Core.Abstractions;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.Backup;

public class BackupDifferTests
{
    private readonly BackupDiffer _differ = new();

    private static ScannedEntry Entry(string path, long size, DateTimeOffset modifiedAt, bool isLink = false) =>
        new(path, $@"C:\src\{path}", size, modifiedAt, isLink, isLink ? @"C:\target" : null);

    [Fact]
    public void A_path_absent_from_current_state_is_classified_as_added()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _differ.Diff([Entry("new.txt", 10, now)], new Dictionary<string, CurrentFileState>());

        var change = Assert.Single(result.Pending);
        Assert.Equal(PendingChangeKind.Added, change.Kind);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_path_with_matching_size_and_mtime_is_unchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState> { ["same.txt"] = new("same.txt", "hash", 10, now) };

        var result = _differ.Diff([Entry("same.txt", 10, now)], current);

        Assert.Empty(result.Pending);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_path_with_a_different_size_is_classified_as_changed()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState> { ["file.txt"] = new("file.txt", "hash", 10, now) };

        var result = _differ.Diff([Entry("file.txt", 20, now)], current);

        var change = Assert.Single(result.Pending);
        Assert.Equal(PendingChangeKind.Changed, change.Kind);
    }

    [Fact]
    public void A_path_with_a_different_modified_time_is_classified_as_changed()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState> { ["file.txt"] = new("file.txt", "hash", 10, now) };

        var result = _differ.Diff([Entry("file.txt", 10, now.AddMinutes(1))], current);

        var change = Assert.Single(result.Pending);
        Assert.Equal(PendingChangeKind.Changed, change.Kind);
    }

    [Fact]
    public void A_current_path_missing_from_the_scan_is_classified_as_deleted()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState> { ["gone.txt"] = new("gone.txt", "hash", 10, now) };

        var result = _differ.Diff([], current);

        Assert.Empty(result.Pending);
        Assert.Equal(["gone.txt"], result.DeletedPaths);
    }

    [Fact]
    public void A_symlink_entry_is_never_diffed_as_content()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _differ.Diff([Entry("link", 0, now, isLink: true)], new Dictionary<string, CurrentFileState>());

        Assert.Empty(result.Pending);
        Assert.Empty(result.DeletedPaths);
    }
}
