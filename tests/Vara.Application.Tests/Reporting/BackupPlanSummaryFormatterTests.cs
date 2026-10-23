using Vara.Application.Backup;
using Vara.Application.Reporting;
using Xunit;

namespace Vara.Application.Tests.Reporting;

public class BackupPlanSummaryFormatterTests
{
    [Fact]
    public void Summary_states_nothing_was_changed_and_lists_every_planned_count_and_the_transfer_size()
    {
        var summary = new BackupPlanSummary(
            FilesAdded: 3, FilesChanged: 1, FilesMoved: 2, FilesDeleted: 4, TotalBytesToTransfer: 2048, FailedPaths: []);

        var text = BackupPlanSummaryFormatter.Format(summary);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Dry run - no changes were made",
                "  Added:       3",
                "  Changed:     1",
                "  Moved:       2",
                "  Deleted:     4",
                "  To transfer: 2 KB"),
            text);
    }

    [Fact]
    public void Failed_paths_are_listed_with_their_count()
    {
        var summary = new BackupPlanSummary(0, 0, 0, 0, 0, FailedPaths: ["locked.txt", "denied.db"]);

        var text = BackupPlanSummaryFormatter.Format(summary);

        Assert.Contains("Failed (2):", text);
        Assert.Contains("    - locked.txt", text);
        Assert.Contains("    - denied.db", text);
    }

    [Fact]
    public void The_failed_section_is_omitted_when_nothing_failed()
    {
        var text = BackupPlanSummaryFormatter.Format(new BackupPlanSummary(1, 0, 0, 0, 10, FailedPaths: []));

        Assert.DoesNotContain("Failed", text);
    }
}
