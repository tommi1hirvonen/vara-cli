using System.Text.Json;
using Vara.Application.Backup;
using Vara.Application.Reporting;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.Reporting;

public class BackupRunSummaryJsonTests
{
    private static string Serialize(BackupRunSummaryJson dto) =>
        JsonSerializer.Serialize(dto, BackupRunSummaryJsonContext.Default.BackupRunSummaryJson);

    [Fact]
    public void ToJson_maps_a_completed_real_run_with_mode_executed()
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddMinutes(1);
        var stats = new SnapshotStats(BytesTransferred: 2048, FilesAdded: 3, FilesChanged: 1, FilesMoved: 2, FilesDeleted: 1, FilesFailed: 1);
        var result = new BackupRunResult(42, startedAt, completedAt, stats, FailedPaths: ["locked.txt"]);

        var dto = result.ToJson("my-profile");

        Assert.Equal("executed", dto.Mode);
        Assert.Equal("my-profile", dto.Profile);
        Assert.Equal(startedAt, dto.StartedAt);
        Assert.Equal(completedAt, dto.CompletedAt);
        Assert.False(dto.Cancelled);
        Assert.Equal(3, dto.Added);
        Assert.Equal(1, dto.Changed);
        Assert.Equal(2, dto.Moved);
        Assert.Equal(1, dto.Deleted);
        Assert.Equal(1, dto.Failed);
        Assert.Equal(["locked.txt"], dto.FailedPaths);
        Assert.Equal(2048, dto.BytesTransferred);

        var json = Serialize(dto);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal("executed", root.GetProperty("mode").GetString());
        Assert.Equal("my-profile", root.GetProperty("profile").GetString());
        Assert.False(root.GetProperty("cancelled").GetBoolean());
        Assert.Equal(3, root.GetProperty("added").GetInt32());
        Assert.Equal(1, root.GetProperty("changed").GetInt32());
        Assert.Equal(2, root.GetProperty("moved").GetInt32());
        Assert.Equal(1, root.GetProperty("deleted").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.Equal("locked.txt", root.GetProperty("failedPaths")[0].GetString());
        Assert.Equal(2048, root.GetProperty("bytesTransferred").GetInt64());
    }

    [Fact]
    public void ToJson_maps_a_dry_run_plan_summary_with_mode_dry_run_and_cancelled_always_false()
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddSeconds(5);
        var summary = new BackupPlanSummary(
            FilesAdded: 4, FilesChanged: 2, FilesMoved: 1, FilesDeleted: 3,
            TotalBytesToTransfer: 4096, FailedPaths: ["unreadable/subtree", "denied.db"]);

        var dto = summary.ToJson("my-profile", startedAt, completedAt);

        Assert.Equal("dry-run", dto.Mode);
        Assert.Equal("my-profile", dto.Profile);
        Assert.Equal(startedAt, dto.StartedAt);
        Assert.Equal(completedAt, dto.CompletedAt);
        Assert.False(dto.Cancelled);
        Assert.Equal(4, dto.Added);
        Assert.Equal(2, dto.Changed);
        Assert.Equal(1, dto.Moved);
        Assert.Equal(3, dto.Deleted);
        Assert.Equal(2, dto.Failed);
        Assert.Equal(["unreadable/subtree", "denied.db"], dto.FailedPaths);
        Assert.Equal(4096, dto.BytesTransferred);

        var json = Serialize(dto);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal("dry-run", root.GetProperty("mode").GetString());
        Assert.False(root.GetProperty("cancelled").GetBoolean());
        Assert.Equal(4, root.GetProperty("added").GetInt32());
        Assert.Equal(2, root.GetProperty("changed").GetInt32());
        Assert.Equal(1, root.GetProperty("moved").GetInt32());
        Assert.Equal(3, root.GetProperty("deleted").GetInt32());
        Assert.Equal(2, root.GetProperty("failed").GetInt32());
        var failedPaths = root.GetProperty("failedPaths");
        Assert.Equal("unreadable/subtree", failedPaths[0].GetString());
        Assert.Equal("denied.db", failedPaths[1].GetString());
        Assert.Equal(4096, root.GetProperty("bytesTransferred").GetInt64());
    }

    [Fact]
    public void ToJson_for_a_dry_run_with_no_failures_serializes_an_empty_failedPaths_array()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var summary = new BackupPlanSummary(0, 0, 0, 0, 0, []);

        var dto = summary.ToJson("my-profile", startedAt, startedAt);
        var json = Serialize(dto);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(0, parsed.RootElement.GetProperty("failedPaths").GetArrayLength());
        Assert.Equal(0, parsed.RootElement.GetProperty("failed").GetInt32());
    }
}
