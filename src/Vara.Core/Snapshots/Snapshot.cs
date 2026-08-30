namespace Vara.Core.Snapshots;

/// <summary>
/// Lifecycle status of a backup run's snapshot record.
/// </summary>
public enum SnapshotStatus
{
    Running,
    Complete,
    Failed,
}

/// <summary>
/// Aggregate statistics for a single backup run, used for the run summary and for
/// history/browsing display.
/// </summary>
public sealed record SnapshotStats(
    long BytesTransferred,
    int FilesAdded,
    int FilesChanged,
    int FilesMoved,
    int FilesDeleted,
    int FilesFailed)
{
    public static SnapshotStats Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// A single recorded backup run for a profile.
/// </summary>
public sealed record Snapshot(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    SnapshotStatus Status,
    SnapshotStats Stats);
