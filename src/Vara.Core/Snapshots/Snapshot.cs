namespace Vara.Core.Snapshots;

/// <summary>
/// Lifecycle status of a backup run's snapshot record.
/// </summary>
public enum SnapshotStatus
{
    Running,
    Complete,
    Failed,

    /// <summary>
    /// The run was deliberately stopped via a single Ctrl+C (a graceful cancellation
    /// that completed a forced checkpoint before exiting), distinct from
    /// <see cref="Failed"/> (a crash, power loss, or a second Ctrl+C forcing immediate
    /// termination) - see backup-execution's "Graceful cancellation via Ctrl+C"
    /// requirement.
    /// </summary>
    Cancelled,
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
