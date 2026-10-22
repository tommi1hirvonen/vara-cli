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
        Action? onFileTransferred = null,
        Action<string>? onFileStarted = null,
        Action<string>? onFileFinished = null,
        CancellationToken cancellationToken = default)
    {
        var counts = new Counts { LastCheckpointUtc = DateTime.UtcNow };
        var failedPaths = new List<string>();
        var reportLock = new Lock();

        foreach (var operation in plan.Operations.Where(operation => operation switch
                 {
                     MoveOperation => true,
                     DeleteOperation => true,
                     LinkOperation => true,
                     AddOperation => false,
                     ChangeOperation => false,
                 }))
        {
            // Cooperative cancellation: stop starting new metadata-only operations once a
            // graceful Ctrl+C stop has been requested. Whatever has already started is
            // allowed to finish (this loop is sequential, so nothing is "in flight" here
            // beyond the current iteration) - see design.md's cancellation-signal decision.
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            ExecuteMetadataOnlyOperation(
                operation, snapshotId, recordedAt,
                counts, failedPaths, reportLock, manifestBatch);
        }

        // Fired exactly once here, unconditionally - even when there are no Move/Delete
        // operations to run above, or no Add/Change operations to run below - so the
        // caller's throughput/ETA clock (BackupProgressCalculator) starts only once byte
        // transfer is about to begin, excluding the Move/Delete pass's own wall-clock
        // time (fix-transfer-clock-start change's design.md).
        onTransferPhaseStarting?.Invoke();

        var transferOperations = plan.Operations.Where(operation => operation switch
        {
            AddOperation => true,
            ChangeOperation => true,
            MoveOperation => false,
            DeleteOperation => false,
            LinkOperation => false,
        }).ToList();

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

                ExecuteTransfer(
                    operation, snapshotId, recordedAt, counts, failedPaths, reportLock,
                    onBytesTransferred, manifestBatch, onFileTransferred, onFileStarted, onFileFinished);
            });

        return new ExecutionOutcome(
            counts.BytesTransferred,
            counts.Added,
            counts.Changed,
            counts.Moved,
            counts.Deleted,
            counts.Failed,
            failedPaths);
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

    private void ExecuteMetadataOnlyOperation(
        PlannedOperation operation,
        long snapshotId,
        DateTimeOffset recordedAt,
        Counts counts,
        List<string> failedPaths,
        Lock reportLock,
        IManifestBatch? manifestBatch)
    {
        try
        {
            switch (operation)
            {
                case MoveOperation move:
                    contentStore.MoveMirrorEntry(move.PreviousRelativePath, move.RelativePath);
                    repository.RecordFileVersion(
                        snapshotId, move.PreviousRelativePath, null, move.KnownContentHash,
                        move.Size, move.SourceModifiedAt, FileChangeKind.Deleted, recordedAt,
                        move.QuickHash, move.QuickHashScheme);
                    repository.RecordFileVersion(
                        snapshotId, move.RelativePath, move.PreviousRelativePath, move.KnownContentHash,
                        move.Size, move.SourceModifiedAt, FileChangeKind.Moved, recordedAt,
                        move.QuickHash, move.QuickHashScheme);
                    counts.Moved++;
                    break;

                case DeleteOperation delete:
                    // Linked entries have no content-store blob to remove.
                    if (!string.IsNullOrEmpty(delete.KnownContentHash))
                    {
                        contentStore.RemoveFromMirror(delete.RelativePath, delete.KnownContentHash);
                    }

                    repository.RecordFileVersion(
                        snapshotId, delete.RelativePath, null, delete.KnownContentHash,
                        delete.Size, delete.SourceModifiedAt, FileChangeKind.Deleted, recordedAt,
                        delete.QuickHash, delete.QuickHashScheme);
                    counts.Deleted++;
                    break;

                case LinkOperation link:
                    // A file-to-link transition must remove the superseded mirror copy.
                    if (!string.IsNullOrEmpty(link.PreviousContentHash))
                    {
                        contentStore.RemoveFromMirror(link.RelativePath, link.PreviousContentHash);
                    }

                    repository.RecordFileVersion(
                        snapshotId, link.RelativePath, null, null,
                        0, DateTimeOffset.MinValue, FileChangeKind.Linked, recordedAt,
                        linkTarget: link.LinkTarget);
                    break;

                case AddOperation:
                case ChangeOperation:
                    throw new InvalidOperationException("A transfer operation cannot be executed as metadata-only.");
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
        Lock reportLock,
        Action<long>? onBytesTransferred,
        IManifestBatch? manifestBatch,
        Action? onFileTransferred,
        Action<string>? onFileStarted,
        Action<string>? onFileFinished)
    {
        var (sourceAbsolutePath, scannedSize, sourceModifiedAt, previousContentHash, changeKind) = operation switch
        {
            AddOperation add => (add.SourceAbsolutePath, add.Size, add.SourceModifiedAt, null, FileChangeKind.Added),
            ChangeOperation change => (
                change.SourceAbsolutePath,
                change.Size,
                change.SourceModifiedAt,
                change.PreviousContentHash,
                FileChangeKind.Changed),
            MoveOperation => throw new InvalidOperationException("A move operation cannot be executed as a content transfer."),
            DeleteOperation => throw new InvalidOperationException("A delete operation cannot be executed as a content transfer."),
            LinkOperation => throw new InvalidOperationException("A link operation cannot be executed as a content transfer."),
        };
        var transferredSuccessfully = false;
        try
        {
            onFileStarted?.Invoke(sourceAbsolutePath);

            string hash;
            long size;
            string quickHash;

            // The actual disk I/O - reading the source and streaming it into the
            // content store - happens outside the lock so it can run concurrently
            // across threads. The quick hash is computed from the same bytes already
            // being read here (see StreamingContentSignature), so recording it costs no
            // extra I/O and lets a future run's move detection pre-filter against this
            // file without a full-content read.
            // When hardlinks are not supported, placing content at the mirror path also
            // streams the whole file as a fallback copy. In that case, chunk progress is
            // apportioned 50% to StoreFromStream and 50% to PlaceAtMirrorPath (reconciling
            // any integer remainder upon completion), so progress advances continuously
            // without exceeding the file's size or doubling the planned total bytes.
            // When hardlinks are supported, StoreFromStream receives 100% of progress,
            // and PlaceAtMirrorPath suppresses callbacks so hardlink-limit fallbacks do
            // not double-count.
            Action<long>? storeCallback = null;
            Action<long>? mirrorCallback = null;
            var reportedSoFar = 0L;

            if (onBytesTransferred is not null)
            {
                if (contentStore.SupportsHardlinks)
                {
                    storeCallback = onBytesTransferred;
                    mirrorCallback = null;
                }
                else
                {
                    var storeSoFar = 0L;
                    storeCallback = chunk =>
                    {
                        storeSoFar += chunk;
                        var target = storeSoFar / 2;
                        var delta = target - reportedSoFar;
                        if (delta > 0)
                        {
                            reportedSoFar += delta;
                            onBytesTransferred(delta);
                        }
                    };

                    var mirrorSoFar = 0L;
                    mirrorCallback = chunk =>
                    {
                        mirrorSoFar += chunk;
                        var target = (scannedSize / 2) + (mirrorSoFar / 2);
                        var delta = target - reportedSoFar;
                        if (delta > 0)
                        {
                            reportedSoFar += delta;
                            onBytesTransferred(delta);
                        }
                    };
                }
            }

            using (var sourceStream = File.OpenRead(sourceAbsolutePath))
            {
                var signature = new StreamingContentSignature(sourceStream, hasher);
                quickHash = signature.ComputeQuickHash();
                (hash, size) = contentStore.StoreFromStream(signature.ReplayFromStart(), storeCallback);
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
            var postTransferInfo = new FileInfo(sourceAbsolutePath);
            if (postTransferInfo.Length != scannedSize || postTransferInfo.LastWriteTimeUtc != sourceModifiedAt)
            {
                lock (reportLock)
                {
                    counts.Failed++;
                    failedPaths.Add(operation.RelativePath);
                    CheckpointIfDue(counts, manifestBatch);
                }

                return;
            }

            contentStore.PlaceAtMirrorPath(hash, operation.RelativePath, mirrorCallback, previousContentHash);

            if (!contentStore.SupportsHardlinks && onBytesTransferred is not null)
            {
                var remainder = size - reportedSoFar;
                if (remainder > 0)
                {
                    onBytesTransferred(remainder);
                }
            }

            lock (reportLock)
            {
                repository.RecordFileVersion(
                    snapshotId, operation.RelativePath, null, hash, size, sourceModifiedAt, changeKind, recordedAt,
                    quickHash, QuickHashPolicy.CurrentScheme);

                counts.BytesTransferred += size;
                if (changeKind == FileChangeKind.Added) counts.Added++; else counts.Changed++;
                CheckpointIfDue(counts, manifestBatch);
                transferredSuccessfully = true;
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
        finally
        {
            onFileFinished?.Invoke(sourceAbsolutePath);
        }

        if (transferredSuccessfully)
        {
            onFileTransferred?.Invoke();
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
