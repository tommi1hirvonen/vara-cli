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
public sealed record PendingChange(ScannedEntry Entry, PendingChangeKind Kind);

/// <summary>The result of the diff stage: candidate changes needing content resolution, and paths no longer present in any source.</summary>
public sealed record DiffResult(IReadOnlyList<PendingChange> Pending, IReadOnlyList<string> DeletedPaths);

/// <summary>A fully resolved operation kind, after move detection.</summary>
public enum PlannedOperationKind
{
    Add,
    Change,
    Move,
    Delete,

    /// <summary>
    /// Records a symlink/junction's presence (and target path) as a manifest row -
    /// metadata-only, like <see cref="Move"/>/<see cref="Delete"/>, since the target is
    /// never followed or copied.
    /// </summary>
    Link,
}

/// <summary>
/// A single resolved operation the execute stage will carry out. For <see cref="PlannedOperationKind.Move"/>
/// and <see cref="PlannedOperationKind.Delete"/>, <see cref="KnownContentHash"/> (and, when known,
/// <see cref="QuickHash"/>/<see cref="QuickHashScheme"/>) is already known (no content read is
/// needed); for <see cref="PlannedOperationKind.Add"/>/<see cref="PlannedOperationKind.Change"/> these
/// are resolved during execution as content is streamed into the content store. For
/// <see cref="PlannedOperationKind.Link"/>, <see cref="KnownContentHash"/> instead carries the
/// link's target path (there is no real content-store hash, since the target is never read) -
/// also known upfront, so no execution-time resolution is needed.
/// <see cref="PreviousContentHash"/> is distinct: for <see cref="PlannedOperationKind.Change"/> it is
/// the *previous* content's hash (already known from the current manifest state at plan time, unlike
/// <see cref="KnownContentHash"/>'s *new*-content meaning above) - used to restore the content
/// store's read-only protection on the superseded blob after the mirror entry is overwritten
/// (protect-hardlinked-mirror-files change's design.md). <c>null</c> for every other operation kind.
/// </summary>
public sealed record PlannedOperation(
    PlannedOperationKind Kind,
    string RelativePath,
    string? PreviousRelativePath,
    string? SourceAbsolutePath,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? KnownContentHash,
    string? QuickHash = null,
    int? QuickHashScheme = null,
    string? PreviousContentHash = null);

/// <summary>
/// The full backup plan: every operation to carry out, and the total bytes that will
/// actually be transferred (excluding moves and deletions, which are near-instant) -
/// known before execution begins, per the progress-reporting spec's "Upfront work
/// estimation" requirement.
/// </summary>
public sealed record BackupPlan(IReadOnlyList<PlannedOperation> Operations, long TotalBytesToTransfer);

/// <summary>Live progress during the execute stage: bytes transferred so far vs. the plan's total.</summary>
public sealed record BackupProgress(long BytesTransferred, long TotalBytes);

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
