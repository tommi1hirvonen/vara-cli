using System.Threading;
using Vara.Core.Abstractions;
using Vara.Core.Hashing;
using Vara.Core.Snapshots;

namespace Vara.Application.Backup;

/// <summary>
/// Carries out a resolved <see cref="BackupPlan"/> against the content store and
/// manifest. Per-operation failures (e.g. a locked file) are caught and recorded
/// rather than aborting the run (backup-execution spec: "Unreadable files do not
/// abort the run"), for Move/Delete against the mirror just as much as for
/// Add/Change reads from the source. Add/Change operations - the only ones that
/// perform real source-disk I/O - run with bounded parallelism suited to NVMe
/// queue depth (design.md); Move/Delete operations are cheap metadata-only changes
/// and run sequentially beforehand. Manifest writes and progress/counter updates
/// are serialized under a lock, since a single SQLite connection is not safe for
/// concurrent use from multiple threads. When <paramref name="manifestBatch"/> (see
/// <see cref="Execute"/>) is supplied, this executor also checkpoints it - commits it
/// and reopens - roughly every <see cref="DefaultCheckpointInterval"/> of elapsed
/// time, so an interruption only discards work performed since the most recent
/// checkpoint rather than the whole run (backup-execution spec's "Manifest writes are
/// checkpointed periodically during a run" requirement, the periodic-checkpointing
/// follow-up to the original batch-manifest-writes change - see design.md). The
/// caller (<see cref="BackupPipeline"/>) still performs one final commit after this
/// executor returns, covering its own trailing outcome update alongside whatever
/// this executor did not already checkpoint.
/// </summary>
public sealed class BackupExecutor(
    IContentStore contentStore,
    ISnapshotRepository repository,
    IHasher hasher,
    int maxDegreeOfParallelism = 0,
    TimeSpan? checkpointInterval = null)
{
    // The most common target is a slower external drive (e.g. an HDD), which performs
    // best when written to by one stream at a time rather than several interleaved
    // ones; the scan stage's own default (Environment.ProcessorCount) is unaffected,
    // since it only reads the source and never touches the target. See design.md of
    // the configure-backup-concurrency change - kept as a single named constant so the
    // default can be revised later without touching call sites.
    private const int DefaultTransferConcurrency = 1;

    // Time-based rather than file/byte-count-based, so the worst-case redo window after
    // an interruption is bounded and predictable regardless of the mix of file sizes in
    // a given run - see the add-backup-checkpoints-and-cancellation change's design.md
    // "Checkpoint trigger" decision. Overridable (see <see cref="checkpointInterval"/>)
    // so tests can observe multiple checkpoints without waiting 30 real seconds.
    private static readonly TimeSpan DefaultCheckpointInterval = TimeSpan.FromSeconds(30);

    private readonly int _maxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : DefaultTransferConcurrency;
    private readonly TimeSpan _checkpointInterval = checkpointInterval ?? DefaultCheckpointInterval;

    public ExecutionOutcome Execute(
        long snapshotId,
        DateTimeOffset recordedAt,
        BackupPlan plan,
        Action<long>? onBytesTransferred = null,
        Action? onTransferPhaseStarting = null,
        IManifestBatch? manifestBatch = null,
        CancellationToken cancellationToken = default)
    {
        var counts = new Counts { LastCheckpointUtc = DateTime.UtcNow };
        var failedPaths = new List<string>();
        var reportLock = new object();

        foreach (var operation in plan.Operations.Where(o => o.Kind is PlannedOperationKind.Move or PlannedOperationKind.Delete or PlannedOperationKind.Link))
        {
            // Cooperative cancellation: stop starting new metadata-only operations once a
            // graceful Ctrl+C stop has been requested. Whatever has already started is
            // allowed to finish (this loop is sequential, so nothing is "in flight" here
            // beyond the current iteration) - see design.md's cancellation-signal decision.
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            ExecuteMetadataOnlyOperation(operation, snapshotId, recordedAt, counts, failedPaths, reportLock, manifestBatch);
        }

        // Fired exactly once here, unconditionally - even when there are no Move/Delete
        // operations to run above, or no Add/Change operations to run below - so the
        // caller's throughput/ETA clock (BackupProgressCalculator) starts only once byte
        // transfer is about to begin, excluding the Move/Delete pass's own wall-clock
        // time (fix-transfer-clock-start change's design.md).
        onTransferPhaseStarting?.Invoke();

        var transferOperations = plan.Operations.Where(o => o.Kind is PlannedOperationKind.Add or PlannedOperationKind.Change).ToList();

        Parallel.ForEach(
            transferOperations,
            new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism },
            operation =>
            {
                // Same cooperative cancellation as above: a worker that dequeues an item
                // after cancellation was requested skips it entirely rather than starting
                // it, instead of aborting a transfer already in progress on another thread.
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ExecuteTransfer(operation, snapshotId, recordedAt, counts, failedPaths, reportLock, onBytesTransferred, manifestBatch);
            });

        return new ExecutionOutcome(counts.BytesTransferred, counts.Added, counts.Changed, counts.Moved, counts.Deleted, counts.Failed, failedPaths);
    }

    /// <summary>
    /// Checkpoints <paramref name="manifestBatch"/> - committing it and reopening a fresh
    /// transaction - if it is supplied and <see cref="_checkpointInterval"/> has elapsed
    /// since the last checkpoint. Must be called while holding <paramref name="counts"/>'s
    /// owning <c>reportLock</c>, since <see cref="Counts.LastCheckpointUtc"/> is otherwise
    /// unsynchronized shared state.
    /// </summary>
    private void CheckpointIfDue(Counts counts, IManifestBatch? manifestBatch)
    {
        if (manifestBatch is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - counts.LastCheckpointUtc < _checkpointInterval)
        {
            return;
        }

        manifestBatch.Commit();
        counts.LastCheckpointUtc = now;
    }

    private void ExecuteMetadataOnlyOperation(PlannedOperation operation, long snapshotId, DateTimeOffset recordedAt, Counts counts, List<string> failedPaths, object reportLock, IManifestBatch? manifestBatch)
    {
        try
        {
            if (operation.Kind == PlannedOperationKind.Move)
            {
                contentStore.MoveMirrorEntry(operation.PreviousRelativePath!, operation.RelativePath);
                repository.RecordFileVersion(
                    snapshotId, operation.PreviousRelativePath!, null, operation.KnownContentHash!,
                    operation.Size, operation.SourceModifiedAt, FileChangeKind.Deleted, recordedAt,
                    operation.QuickHash, operation.QuickHashScheme);
                repository.RecordFileVersion(
                    snapshotId, operation.RelativePath, operation.PreviousRelativePath, operation.KnownContentHash!,
                    operation.Size, operation.SourceModifiedAt, FileChangeKind.Moved, recordedAt,
                    operation.QuickHash, operation.QuickHashScheme);
                counts.Moved++;
            }
            else if (operation.Kind == PlannedOperationKind.Delete)
            {
                // KnownContentHash is null when the path being deleted was itself most
                // recently a Linked entry - there was never a content-store blob for it,
                // so there is nothing to remove from the mirror (and no valid hash to
                // pass into the content store's blob-path resolution).
                if (!string.IsNullOrEmpty(operation.KnownContentHash))
                {
                    contentStore.RemoveFromMirror(operation.RelativePath, operation.KnownContentHash);
                }

                repository.RecordFileVersion(
                    snapshotId, operation.RelativePath, null, operation.KnownContentHash,
                    operation.Size, operation.SourceModifiedAt, FileChangeKind.Deleted, recordedAt,
                    operation.QuickHash, operation.QuickHashScheme);
                counts.Deleted++;
            }
            else
            {
                // Link: no content store I/O for the link target itself - it is never
                // followed or copied. PreviousContentHash, when set, means this is a
                // file-to-link transition (BackupPlanner): the mirror still holds the
                // path's previously tracked content, which is now superseded by the
                // link and must be removed so the mirror doesn't retain it forever.
                if (!string.IsNullOrEmpty(operation.PreviousContentHash))
                {
                    contentStore.RemoveFromMirror(operation.RelativePath, operation.PreviousContentHash);
                }

                repository.RecordFileVersion(
                    snapshotId, operation.RelativePath, null, null,
                    operation.Size, operation.SourceModifiedAt, FileChangeKind.Linked, recordedAt,
                    operation.QuickHash, operation.QuickHashScheme, linkTarget: operation.LinkTarget);
            }

            lock (reportLock)
            {
                CheckpointIfDue(counts, manifestBatch);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (reportLock)
            {
                counts.Failed++;
                failedPaths.Add(operation.RelativePath);
                CheckpointIfDue(counts, manifestBatch);
            }
        }
    }

    private void ExecuteTransfer(
        PlannedOperation operation,
        long snapshotId,
        DateTimeOffset recordedAt,
        Counts counts,
        List<string> failedPaths,
        object reportLock,
        Action<long>? onBytesTransferred,
        IManifestBatch? manifestBatch)
    {
        try
        {
            string hash;
            long size;
            string quickHash;

            // The actual disk I/O - reading the source and streaming it into the
            // content store - happens outside the lock so it can run concurrently
            // across threads. The quick hash is computed from the same bytes already
            // being read here (see StreamingContentSignature), so recording it costs no
            // extra I/O and lets a future run's move detection pre-filter against this
            // file without a full-content read. onBytesTransferred is forwarded directly
            // into both StoreFromStream and PlaceAtMirrorPath as a chunk-level progress
            // callback (stream-large-file-transfer-progress change), so progress advances
            // continuously during a large file's transfer instead of jumping once it
            // completes; the once-per-file summary counters below are unaffected, since
            // they're driven by StoreFromStream's returned size, not by how many times
            // the callback fires.
            using (var sourceStream = File.OpenRead(operation.SourceAbsolutePath!))
            {
                var signature = new StreamingContentSignature(sourceStream, hasher);
                quickHash = signature.ComputeQuickHash();
                (hash, size) = contentStore.StoreFromStream(signature.ReplayFromStart(), onBytesTransferred);
            }

            // Re-stat the source right after the read completes - and before placing
            // anything at the mirror path - and compare against what was observed at scan
            // time. A mismatch means the file was modified after being scanned but before
            // (or while) its content was actually captured above, so the content just read
            // can no longer be trusted as "the file as of SourceModifiedAt" - treat the
            // whole operation as failed and let the next run's incremental scan pick it up
            // again, rather than placing that content at the mirror path or recording a
            // manifest entry under stale metadata (detect-torn-reads-after-transfer
            // change's design.md). Uses a plain FileInfo stat, the same mechanism the
            // scanner itself uses (DirectoryFileSystemScanner.ToEntry).
            var postTransferInfo = new FileInfo(operation.SourceAbsolutePath!);
            if (postTransferInfo.Length != operation.Size || postTransferInfo.LastWriteTimeUtc != operation.SourceModifiedAt)
            {
                lock (reportLock)
                {
                    counts.Failed++;
                    failedPaths.Add(operation.RelativePath);
                    CheckpointIfDue(counts, manifestBatch);
                }

                return;
            }

            contentStore.PlaceAtMirrorPath(hash, operation.RelativePath, onBytesTransferred, operation.PreviousContentHash);

            lock (reportLock)
            {
                var changeKind = operation.Kind == PlannedOperationKind.Add ? FileChangeKind.Added : FileChangeKind.Changed;
                repository.RecordFileVersion(
                    snapshotId, operation.RelativePath, null, hash, size, operation.SourceModifiedAt, changeKind, recordedAt,
                    quickHash, QuickHashPolicy.CurrentScheme);

                counts.BytesTransferred += size;
                if (changeKind == FileChangeKind.Added) counts.Added++; else counts.Changed++;
                CheckpointIfDue(counts, manifestBatch);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (reportLock)
            {
                counts.Failed++;
                failedPaths.Add(operation.RelativePath);
                CheckpointIfDue(counts, manifestBatch);
            }
        }
    }

    private sealed class Counts
    {
        public long BytesTransferred;
        public int Added;
        public int Changed;
        public int Moved;
        public int Deleted;
        public int Failed;
        public DateTime LastCheckpointUtc;
    }
}
