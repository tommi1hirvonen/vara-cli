using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Core.Tests.Snapshots;

public class SnapshotStatsTests
{
    [Fact]
    public void Empty_stats_are_all_zero()
    {
        var stats = SnapshotStats.Empty;

        Assert.Equal(0, stats.BytesTransferred);
        Assert.Equal(0, stats.FilesAdded);
        Assert.Equal(0, stats.FilesChanged);
        Assert.Equal(0, stats.FilesMoved);
        Assert.Equal(0, stats.FilesDeleted);
        Assert.Equal(0, stats.FilesFailed);
    }
}

public class FileVersionRecordTests
{
    [Fact]
    public void Moved_record_carries_the_previous_path()
    {
        var recordedAt = DateTimeOffset.UtcNow;
        var record = new FileVersionRecord(
            Id: 1,
            SnapshotId: 10,
            RelativePath: @"Documents\report.pdf",
            PreviousRelativePath: @"Downloads\report.pdf",
            ContentHash: "abc123",
            Size: 2048,
            SourceModifiedAt: recordedAt,
            ChangeKind: FileChangeKind.Moved,
            RecordedAt: recordedAt);

        Assert.Equal(FileChangeKind.Moved, record.ChangeKind);
        Assert.Equal(@"Downloads\report.pdf", record.PreviousRelativePath);
    }

    [Fact]
    public void Added_record_has_no_previous_path()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new FileVersionRecord(1, 10, "file.txt", null, "hash", 10, now, FileChangeKind.Added, now);

        Assert.Null(record.PreviousRelativePath);
    }
}
