using Vara.Application.Backup;
using Vara.Application.Reporting;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.Reporting;

public class BackupRunSummaryFormatterTests
{
    [Fact]
    public void Summary_includes_all_counts_bytes_and_elapsed_time()
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddMinutes(1).AddSeconds(30);
        var stats = new SnapshotStats(BytesTransferred: 2048, FilesAdded: 3, FilesChanged: 1, FilesMoved: 2, FilesDeleted: 1, FilesFailed: 0);
        var result = new BackupRunResult(SnapshotId: 42, startedAt, completedAt, stats, FailedPaths: []);

        var summary = BackupRunSummaryFormatter.Format(result);

        Assert.Contains("42", summary);
        Assert.Contains("1m 30s", summary);
        Assert.Contains("Added:       3", summary);
        Assert.Contains("Changed:     1", summary);
        Assert.Contains("Moved:       2", summary);
        Assert.Contains("Deleted:     1", summary);
        Assert.Contains("2 KB", summary);
    }

    [Fact]
    public void Summary_lists_failed_files_when_present()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new BackupRunResult(1, now, now, SnapshotStats.Empty, FailedPaths: ["locked.txt", "denied.db"]);

        var summary = BackupRunSummaryFormatter.Format(result);

        Assert.Contains("Failed (2):", summary);
        Assert.Contains("locked.txt", summary);
        Assert.Contains("denied.db", summary);
    }

    [Fact]
    public void Summary_omits_the_failed_section_when_there_are_no_failures()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new BackupRunResult(1, now, now, SnapshotStats.Empty, FailedPaths: []);

        var summary = BackupRunSummaryFormatter.Format(result);

        Assert.DoesNotContain("Failed", summary);
    }

    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1024 * 1024 * 3, "3 MB")]
    public void FormatBytes_uses_the_appropriate_unit(long bytes, string expected)
    {
        Assert.Equal(expected, BackupRunSummaryFormatter.FormatBytes(bytes));
    }
}
