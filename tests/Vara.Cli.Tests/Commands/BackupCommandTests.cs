using Vara.Application.Backup;
using Vara.Cli.Commands;
using Vara.Cli.Presentation;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class BackupCommandTests
{
    private static BackupRunResult MakeResult(IReadOnlyList<string> failedPaths)
    {
        var startedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var stats = SnapshotStats.Empty with { FilesFailed = failedPaths.Count };
        return new BackupRunResult(1, startedAt, startedAt.AddSeconds(1), stats, failedPaths);
    }

    [Fact]
    public void ResolveOutcome_is_success_when_no_files_failed()
    {
        var result = MakeResult([]);

        Assert.Equal(BackupProgressOutcome.Success, BackupCommand.ResolveOutcome(result));
    }

    [Fact]
    public void ResolveOutcome_is_partial_failure_when_one_or_more_files_failed()
    {
        var result = MakeResult(["some/failed/path.txt"]);

        Assert.Equal(BackupProgressOutcome.PartialFailure, BackupCommand.ResolveOutcome(result));
    }
}
