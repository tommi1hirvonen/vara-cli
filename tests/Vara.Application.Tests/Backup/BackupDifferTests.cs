using Vara.Application.Backup;
using Vara.Core.Abstractions;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.Backup;

public class BackupDifferTests
{
    private readonly BackupDiffer _differ = new();
    private static readonly ScanFailure[] NoFailures = [];

    private static ScannedEntry Entry(string path, long size, DateTimeOffset modifiedAt, bool isLink = false) =>
        new(path, $@"C:\src\{path}", size, modifiedAt, isLink, isLink ? @"C:\target" : null);

    [Fact]
    public void A_path_absent_from_current_state_is_classified_as_added()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _differ.Diff([Entry("new.txt", 10, now)], new Dictionary<string, CurrentFileState>(), NoFailures);

        var change = Assert.Single(result.Pending);
        Assert.Equal(PendingChangeKind.Added, change.Kind);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_path_with_matching_size_and_mtime_is_unchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState> { ["same.txt"] = new("same.txt", "hash", 10, now) };

        var result = _differ.Diff([Entry("same.txt", 10, now)], current, NoFailures);

        Assert.Empty(result.Pending);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_path_differing_only_in_casing_with_matching_size_and_mtime_is_unchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase)
        {
            ["Photo.JPG"] = new("Photo.JPG", "hash", 10, now)
        };

        var result = _differ.Diff([Entry("photo.jpg", 10, now)], current, NoFailures);

        Assert.Empty(result.Pending);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_path_with_a_different_size_is_classified_as_changed()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState> { ["file.txt"] = new("file.txt", "hash", 10, now) };

        var result = _differ.Diff([Entry("file.txt", 20, now)], current, NoFailures);

        var change = Assert.Single(result.Pending);
        Assert.Equal(PendingChangeKind.Changed, change.Kind);
    }

    [Fact]
    public void A_path_with_a_different_modified_time_is_classified_as_changed()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState> { ["file.txt"] = new("file.txt", "hash", 10, now) };

        var result = _differ.Diff([Entry("file.txt", 10, now.AddMinutes(1))], current, NoFailures);

        var change = Assert.Single(result.Pending);
        Assert.Equal(PendingChangeKind.Changed, change.Kind);
    }

    [Fact]
    public void A_current_path_missing_from_the_scan_is_classified_as_deleted()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState> { ["gone.txt"] = new("gone.txt", "hash", 10, now) };

        var result = _differ.Diff([], current, NoFailures);

        Assert.Empty(result.Pending);
        Assert.Equal(["gone.txt"], result.DeletedPaths);
    }

    [Fact]
    public void A_new_symlink_entry_is_recorded_as_linked_and_never_treated_as_deleted()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _differ.Diff([Entry("link", 0, now, isLink: true)], new Dictionary<string, CurrentFileState>(), NoFailures);

        var change = Assert.Single(result.Pending);
        Assert.Equal(PendingChangeKind.Linked, change.Kind);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_path_previously_tracked_as_a_regular_file_now_scanned_as_a_symlink_is_classified_as_linked()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState> { ["was-file"] = new("was-file", "hash", 10, now.AddMinutes(-1)) };

        var result = _differ.Diff([Entry("was-file", 0, now, isLink: true)], current, NoFailures);

        var change = Assert.Single(result.Pending);
        Assert.Equal(PendingChangeKind.Linked, change.Kind);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_symlink_present_unchanged_across_two_consecutive_diffs_is_not_added_deleted_or_changed_on_the_second_run()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState>
        {
            ["link"] = new("link", @"C:\target", 0, DateTimeOffset.MinValue, IsLinked: true),
        };

        var result = _differ.Diff([Entry("link", 0, now, isLink: true)], current, NoFailures);

        Assert.Empty(result.Pending);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_failure_whose_mirror_path_exactly_matches_a_current_state_path_suppresses_it()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState> { [@"C\src\locked.txt"] = new(@"C\src\locked.txt", "hash", 10, now) };
        var failures = new[] { new ScanFailure("locked.txt", ScanFailureReason.UnreadableEntry, @"C\src\locked.txt") };

        var result = _differ.Diff([], current, failures);

        Assert.Empty(result.Pending);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_failure_whose_mirror_path_is_an_ancestor_directory_suppresses_every_path_nested_under_it()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState>
        {
            [@"C\src\denied-dir\a.txt"] = new(@"C\src\denied-dir\a.txt", "hash", 10, now),
            [@"C\src\denied-dir\nested\b.txt"] = new(@"C\src\denied-dir\nested\b.txt", "hash", 10, now),
        };
        var failures = new[] { new ScanFailure("denied-dir", ScanFailureReason.UnreadableDirectory, @"C\src\denied-dir") };

        var result = _differ.Diff([], current, failures);

        Assert.Empty(result.Pending);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_source_unavailable_failure_suppresses_every_path_under_that_source()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState>
        {
            [@"D\photos\a.jpg"] = new(@"D\photos\a.jpg", "hash", 10, now),
        };
        var failures = new[] { new ScanFailure(@"D:\photos", ScanFailureReason.SourceUnavailable, @"D\photos") };

        var result = _differ.Diff([], current, failures);

        Assert.Empty(result.Pending);
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void A_path_sharing_a_prefix_without_a_full_segment_match_is_not_suppressed()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState>
        {
            [@"C\Users\john2\file.txt"] = new(@"C\Users\john2\file.txt", "hash", 10, now),
        };
        var failures = new[] { new ScanFailure(@"C:\Users\john", ScanFailureReason.SourceUnavailable, @"C\Users\john") };

        var result = _differ.Diff([], current, failures);

        Assert.Empty(result.Pending);
        Assert.Equal([@"C\Users\john2\file.txt"], result.DeletedPaths);
    }

    [Fact]
    public void A_path_unrelated_to_any_failure_is_still_classified_as_deleted()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, CurrentFileState>
        {
            [@"C\src\gone.txt"] = new(@"C\src\gone.txt", "hash", 10, now),
            [@"C\src\denied-dir\a.txt"] = new(@"C\src\denied-dir\a.txt", "hash", 10, now),
        };
        var failures = new[] { new ScanFailure("denied-dir", ScanFailureReason.UnreadableDirectory, @"C\src\denied-dir") };

        var result = _differ.Diff([], current, failures);

        Assert.Empty(result.Pending);
        Assert.Equal([@"C\src\gone.txt"], result.DeletedPaths);
    }
}
