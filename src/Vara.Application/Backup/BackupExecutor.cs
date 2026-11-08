using System.Threading.Channels;
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
/// and run sequentially beforehand. Add/Change workers never touch the repository: each
/// publishes an immutable per-file result to a channel, and the thread that called
/// <see cref="Execute"/> is the single recorder that drains it and owns the manifest
/// writes, counters, failed paths and checkpoint clock, since a single SQLite connection
/// is not safe for concurrent use from multiple threads. When <paramref name="manifestBatch"/> (see
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

        foreach (var operation in plan.Operations.OfType<MetadataOperation>())
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
                counts, failedPaths, manifestBatch);
        }

        // Fired exactly once here, unconditionally - even when there are no Move/Delete
        // operations to run above, or no Add/Change operations to run below - so the
        // caller's throughput/ETA clock (BackupProgressCalculator) starts only once byte
        // transfer is about to begin, excluding the Move/Delete pass's own wall-clock
        // time (fix-transfer-clock-start change's design.md).
        onTransferPhaseStarting?.Invoke();

        var transferOperations = plan.Operations.OfType<TransferOperation>().ToList();

        // Workers run off-thread and only publish immutable results; this (the calling)
        // thread is the single recorder that owns the repository, manifest batch, counters,
        // failed paths and checkpoint clock, so no lock is needed. The channel is unbounded
        // so a worker's TryWrite never blocks on the recorder.
        var results = Channel.CreateUnbounded<TransferResult>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // Distinct from the graceful Ctrl+C token: cancelling this one only happens when the
        // recorder itself fails, to stop workers from starting any further transfers.
        using var abort = new CancellationTokenSource();

        var transferTask = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(
                    transferOperations,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _maxDegreeOfParallelism,
                        CancellationToken = abort.Token,
                    },
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
                            operation, results.Writer, onBytesTransferred, onFileStarted, onFileFinished);
                    });
            }
            finally
            {
                results.Writer.Complete();
            }
        }, CancellationToken.None);

        try
        {
            // Blocking here is acceptable: this is the pipeline's own thread, not a thread-pool
            // thread, and a console app has no SynchronizationContext to deadlock on.
            while (results.Reader.WaitToReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult())
            {
                while (results.Reader.TryRead(out var result))
                {
                    RecordTransferResult(
                        result, snapshotId, recordedAt, counts, failedPaths, manifestBatch, onFileTransferred);
                }
            }
        }
        catch
        {
            // Stop workers and wait for them, so none is still writing to the content store
            // when the caller records the failure; then surface the recorder's own exception.
            abort.Cancel();
            try
            {
                transferTask.Wait(CancellationToken.None);
            }
            catch (AggregateException)
            {
            }

            throw;
        }

        // Rethrows an unexpected worker exception (every result produced before it has
        // already been recorded above), with the same shape Parallel.ForEach produced
        // when it ran on the calling thread.
        transferTask.GetAwaiter().GetResult();

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
    /// since the last checkpoint. Must only be called from the recorder (the thread that
    /// called <see cref="Execute"/>), which owns <paramref name="counts"/>.
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

    private void RecordTransferResult(
        TransferResult result,
        long snapshotId,
        DateTimeOffset recordedAt,
        Counts counts,
        List<string> failedPaths,
        IManifestBatch? manifestBatch,
        Action? onFileTransferred)
    {
        switch (result)
        {
            case Transferred transferred:
                repository.RecordFileVersion(
                    snapshotId, transferred.RelativePath, null, transferred.Hash, transferred.Size,
                    transferred.SourceModifiedAt, transferred.ChangeKind, recordedAt,
                    transferred.QuickHash, QuickHashPolicy.CurrentScheme);

                counts.BytesTransferred += transferred.Size;
                if (transferred.ChangeKind == FileChangeKind.Added) counts.Added++; else counts.Changed++;
                CheckpointIfDue(counts, manifestBatch);
                onFileTransferred?.Invoke();
                break;

            case Failed failed:
                counts.Failed++;
                failedPaths.Add(failed.RelativePath);
                CheckpointIfDue(counts, manifestBatch);
                break;
        }
    }

    private void ExecuteMetadataOnlyOperation(
        MetadataOperation operation,
        long snapshotId,
        DateTimeOffset recordedAt,
        Counts counts,
        List<string> failedPaths,
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
            }

            CheckpointIfDue(counts, manifestBatch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counts.Failed++;
            failedPaths.Add(operation.RelativePath);
            CheckpointIfDue(counts, manifestBatch);
        }
    }

    private void ExecuteTransfer(
        TransferOperation operation,
        ChannelWriter<TransferResult> results,
        Action<long>? onBytesTransferred,
        Action<string>? onFileStarted,
        Action<string>? onFileFinished)
    {
        var sourceAbsolutePath = operation.SourceAbsolutePath;
        var scannedSize = operation.Size;
        var sourceModifiedAt = operation.SourceModifiedAt;
        var (previousContentHash, changeKind) = operation switch
        {
            AddOperation => ((string?)null, FileChangeKind.Added),
            ChangeOperation change => (change.PreviousContentHash, FileChangeKind.Changed),
        };
        TransferResult? result = null;
        try
        {
            onFileStarted?.Invoke(sourceAbsolutePath);

            string hash;
            long size;
            string quickHash;

            // The actual disk I/O - reading the source and streaming it into the
            // content store - happens on the worker threads, concurrently with the
            // recorder's manifest writes. The quick hash is computed from the same bytes already
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
                result = new Failed(operation.RelativePath);
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

            result = new Transferred(
                operation.RelativePath, hash, size, sourceModifiedAt, changeKind, quickHash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = new Failed(operation.RelativePath);
        }
        finally
        {
            // Finished fires before the result is published: the file leaves the active
            // transfer list once its I/O is done, not once its manifest row is written.
            // No result is published when an unexpected exception is propagating.
            onFileFinished?.Invoke(sourceAbsolutePath);
            if (result is not null)
            {
                results.TryWrite(result);
            }
        }
    }

    private closed record TransferResult(string RelativePath);

    private sealed record Transferred(
        string RelativePath,
        string Hash,
        long Size,
        DateTimeOffset SourceModifiedAt,
        FileChangeKind ChangeKind,
        string QuickHash) : TransferResult(RelativePath);

    private sealed record Failed(string RelativePath) : TransferResult(RelativePath);

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
