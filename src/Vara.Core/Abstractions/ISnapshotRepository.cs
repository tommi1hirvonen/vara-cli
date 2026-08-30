using Vara.Core.Snapshots;

namespace Vara.Core.Abstractions;

/// <summary>
/// Provides access to a profile's manifest: the durable record of snapshots and
/// file-version history that backs the backup-execution, snapshot-history, and
/// retention-pruning capabilities. One repository instance is scoped to one profile.
/// </summary>
public interface ISnapshotRepository : IDisposable
{
    /// <summary>
    /// On startup, reconciles any snapshot left in <see cref="SnapshotStatus.Running"/> by a
    /// previous, interrupted run into <see cref="SnapshotStatus.Failed"/>.
    /// </summary>
    void ReconcileIncompleteSnapshots();

    /// <summary>
    /// Starts a new snapshot in the <see cref="SnapshotStatus.Running"/> state and returns its id.
    /// </summary>
    long BeginSnapshot(DateTimeOffset startedAt);

    /// <summary>
    /// Records a single file-version event as part of the given snapshot.
    /// </summary>
    void RecordFileVersion(
        long snapshotId,
        string relativePath,
        string? previousRelativePath,
        string contentHash,
        long size,
        DateTimeOffset sourceModifiedAt,
        FileChangeKind changeKind,
        DateTimeOffset recordedAt);

    /// <summary>
    /// Marks a snapshot as successfully completed with its final statistics.
    /// </summary>
    void CompleteSnapshot(long snapshotId, DateTimeOffset completedAt, SnapshotStats stats);

    /// <summary>
    /// Marks a snapshot as failed (e.g. interrupted) with whatever statistics were gathered.
    /// </summary>
    void FailSnapshot(long snapshotId, DateTimeOffset failedAt, SnapshotStats stats);

    /// <summary>
    /// The current (latest, non-deleted) state of every tracked path, keyed by relative path.
    /// </summary>
    IReadOnlyDictionary<string, CurrentFileState> GetCurrentState();

    /// <summary>
    /// All recorded snapshots for this profile, most recent first.
    /// </summary>
    IReadOnlyList<Snapshot> ListSnapshots();

    /// <summary>
    /// The most recently completed snapshot, if any.
    /// </summary>
    Snapshot? GetLastCompletedSnapshot();

    /// <summary>
    /// The full recorded history of a path, most recent first. Empty if the path was never tracked.
    /// </summary>
    IReadOnlyList<FileVersionRecord> GetFileHistory(string relativePath);

    /// <summary>
    /// The version of a path that was current as of <paramref name="asOf"/>, or <c>null</c>
    /// if the path had no recorded version at that time.
    /// </summary>
    FileVersionRecord? FindVersionAsOf(string relativePath, DateTimeOffset asOf);

    /// <summary>
    /// Deletes a snapshot record and its file-version rows (used by pruning).
    /// </summary>
    void DeleteSnapshot(long snapshotId);

    /// <summary>
    /// Prunes the given snapshots: deletes their file-version rows, except any row that
    /// is still the current (latest, non-deleted) state of its path - such a row is
    /// preserved regardless of how old its owning snapshot is, since losing it would
    /// silently orphan a still-live file from <see cref="GetCurrentState"/>. A
    /// snapshot's own record is only deleted once none of its rows remain. Returns the
    /// number of snapshot records actually deleted.
    /// </summary>
    int PruneSnapshots(IReadOnlyList<long> snapshotIds);

    /// <summary>
    /// Content hashes still referenced by at least one remaining file-version row (across
    /// all snapshots, including tombstoned/deleted entries). Used together with
    /// <see cref="IContentStore.ListAllStoredHashes"/> to compute which physically stored
    /// blobs are safe to garbage-collect after pruning.
    /// </summary>
    IReadOnlySet<string> GetAllReferencedContentHashes();
}
