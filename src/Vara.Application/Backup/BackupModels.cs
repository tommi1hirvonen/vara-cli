using Vara.Core.Abstractions;
using Vara.Core.Snapshots;

namespace Vara.Application.Backup;

/// <summary>Pre-move-detection classification of a scanned entry against the manifest.</summary>
public enum PendingChangeKind
{
    Added,
    Changed,

    /// <summary>
    /// A symlink/junction newly observed at this path, or replacing a previously
    /// tracked regular file - never a move candidate (see <see cref="BackupPlanner"/>).
    /// </summary>
    Linked,
}

/// <summary>A scanned entry whose content differs from (or is absent from) the current manifest state.</summary>
public sealed record PendingChange(ScanItem Entry, PendingChangeKind Kind);

/// <summary>The result of the diff stage: candidate changes needing content resolution, and paths no longer present in any source.</summary>
public sealed record DiffResult(IReadOnlyList<PendingChange> Pending, IReadOnlyList<string> DeletedPaths);

/// <summary>
/// The full backup plan: every operation to carry out, and the total bytes and
/// Add/Change files that will actually be transferred (excluding metadata-only
/// operations) - known before execution begins, per the progress-reporting spec's
/// "Upfront work estimation" requirement.
/// </summary>
public sealed record BackupPlan(IReadOnlyList<PlannedOperation> Operations, long TotalBytesToTransfer)
{
    /// <summary>The planned Add/Change operations that will transfer file content.</summary>
    public int TotalFilesToTransfer => Operations.Count(o => o is AddOperation or ChangeOperation);
}

/// <summary>Live execute-stage progress: successfully transferred files and bytes vs. planned totals.</summary>
public sealed record BackupProgress(
    long BytesTransferred,
    long TotalBytes,
    int FilesTransferred = 0,
    int TotalFiles = 0,
    IReadOnlyList<string>? ActivePaths = null);

/// <summary>The outcome of executing a plan: final counts, bytes moved, and any files that failed.</summary>
public sealed record ExecutionOutcome(
    long BytesTransferred,
    int FilesAdded,
    int FilesChanged,
    int FilesMoved,
    int FilesDeleted,
    int FilesFailed,
    IReadOnlyList<string> FailedPaths);

/// <summary>
/// The result of a dry run (<see cref="BackupPipeline.PlanOnly"/>): the planned
/// added/changed/moved/deleted counts and total bytes that would be transferred, plus
/// any paths that could not be scanned (and would therefore be skipped by a real run),
/// without performing any writes - see backup-execution's "Dry-run mode" requirement.
/// </summary>
public sealed record BackupPlanSummary(
    int FilesAdded,
    int FilesChanged,
    int FilesMoved,
    int FilesDeleted,
    long TotalBytesToTransfer,
    IReadOnlyList<string> FailedPaths);

/// <summary>The final result of a full backup run, for CLI run-summary reporting.</summary>
public sealed record BackupRunResult(
    long SnapshotId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    SnapshotStats Stats,
    IReadOnlyList<string> FailedPaths,
    bool Cancelled = false)
{
    public TimeSpan Elapsed => CompletedAt - StartedAt;
}
