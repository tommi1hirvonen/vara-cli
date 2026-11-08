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
/// and run sequentially beforehand. Add/Change workers never touch the repository or
/// invoke a callback: each publishes immutable <see cref="TransferEvent"/>s to a channel,
/// and the body of <see cref="ExecuteAsync"/> is the single recorder that awaits it and
/// owns every callback, the manifest writes, counters, failed paths and checkpoint clock.
/// The recorder may resume on different thread-pool threads between awaits, but it never
/// runs concurrently with itself, which is all a single SQLite connection requires.
/// When <paramref name="manifestBatch"/> (see
/// <see cref="ExecuteAsync"/>) is supplied, this executor also checkpoints it - commits it
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

    public async Task<ExecutionOutcome> ExecuteAsync(
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

        // Workers run in a pool and only publish immutable events; the body of this method is
        // the single recorder that owns the repository, manifest batch, counters, failed paths,
        // checkpoint clock and every callback, so no lock is needed. The channel is unbounded
        // so a worker's TryWrite never waits on the recorder.
        var events = Channel.CreateUnbounded<TransferEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // Distinct from the graceful Ctrl+C token: cancelled only when the recorder fails or a
        // worker crashes, to stop the pool from starting any further transfers.
        using var stopTransfers = new CancellationTokenSource();

        var recorder = new TransferRecorder(
            repository, snapshotId, recordedAt, counts, failedPaths,
            () => CheckpointIfDue(counts, manifestBatch),
            stopTransfers,
            onBytesTransferred, onFileStarted, onFileFinished, onFileTransferred);

        var pool = RunPoolAsync(
            transferOperations,
            events.Writer,
            reportBytes: onBytesTransferred is not null,
            shouldStop: () => cancellationToken.IsCancellationRequested || stopTransfers.IsCancellationRequested);

        try
        {
            await foreach (var transferEvent in events.Reader.ReadAllAsync(CancellationToken.None))
            {
                recorder.Record(transferEvent);
            }
        }
        finally
        {
            // Stop the pool and wait for it, so no worker is still writing to the content store
            // when the caller records a failure. The pool never throws (crashes are events), so
            // awaiting it cannot mask the recorder's own exception.
            stopTransfers.Cancel();
            await pool;
        }

        // Every event produced before a crash has been recorded above.
        if (recorder.Crashes.Count > 0)
        {
            throw new AggregateException(recorder.Crashes);
        }

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
    /// Runs the transfers with bounded parallelism and completes <paramref name="writer"/>
    /// when done. Never throws: <see cref="ExecuteTransfer"/> turns every exception into an event.
    /// </summary>
    private async Task RunPoolAsync(
        List<TransferOperation> operations,
        ChannelWriter<TransferEvent> writer,
        bool reportBytes,
        Func<bool> shouldStop)
    {
        try
        {
            await Parallel.ForEachAsync(
                operations,
                new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism },
                (operation, _) =>
                {
                    // Cooperative cancellation: a worker that dequeues an item after a stop was
                    // requested skips it instead of starting it. Neither token is handed to the
                    // pool, because that would throw and could abandon queued bookkeeping.
                    if (!shouldStop())
                    {
                        // The body is deliberately synchronous (blocking file I/O) and returns a
                        // completed ValueTask; the pool still reaches full concurrency.
                        ExecuteTransfer(operation, writer, reportBytes);
                    }

                    return ValueTask.CompletedTask;
                });
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// Checkpoints <paramref name="manifestBatch"/> - committing it and reopening a fresh
    /// transaction - if it is supplied and <see cref="_checkpointInterval"/> has elapsed
    /// since the last checkpoint. Must only be called from the recorder, which owns
    /// <paramref name="counts"/>.
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
        ChannelWriter<TransferEvent> events,
        bool reportBytes)
    {
        var sourceAbsolutePath = operation.SourceAbsolutePath;
        events.TryWrite(new FileStarted(sourceAbsolutePath));

        TransferEvent outcome;
        try
        {
            outcome = CopyToStore(operation, events, reportBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome = new Failed(operation.RelativePath);
        }
        catch (Exception ex)
        {
            // An unexpected exception must not escape the pool: it becomes a value the
            // recorder collects after draining everything produced before it.
            outcome = new Crashed(operation.RelativePath, ex);
        }

        // Finished is published before the outcome: the file leaves the active transfer
        // list once its I/O is done, not once its manifest row is written.
        events.TryWrite(new FileFinished(sourceAbsolutePath));
        events.TryWrite(outcome);
    }

    private TransferEvent CopyToStore(
        TransferOperation operation,
        ChannelWriter<TransferEvent> events,
        bool reportBytes)
    {
        var sourceAbsolutePath = operation.SourceAbsolutePath;
        var scannedSize = operation.Size;
        var sourceModifiedAt = operation.SourceModifiedAt;
        var (previousContentHash, changeKind) = operation switch
        {
            AddOperation => ((string?)null, FileChangeKind.Added),
            ChangeOperation change => (change.PreviousContentHash, FileChangeKind.Changed),
        };
        Action<long>? onBytesTransferred = reportBytes
            ? count => events.TryWrite(new BytesCopied(count))
            : null;

        string hash;
        long size;
        string quickHash;

        // The actual disk I/O - reading the source and streaming it into the
        // content store - happens on the pool's threads, concurrently with the
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
            return new Failed(operation.RelativePath);
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

        return new Transferred(
            operation.RelativePath, hash, size, sourceModifiedAt, changeKind, quickHash);
    }

    /// <summary>
    /// An immutable fact published by a pool worker. The recorder turns each one into the
    /// matching callback, manifest write or counter update.
    /// </summary>
    private closed record TransferEvent;

    private sealed record FileStarted(string SourceAbsolutePath) : TransferEvent;

    private sealed record BytesCopied(long Count) : TransferEvent;

    private sealed record FileFinished(string SourceAbsolutePath) : TransferEvent;

    private sealed record Transferred(
        string RelativePath,
        string Hash,
        long Size,
        DateTimeOffset SourceModifiedAt,
        FileChangeKind ChangeKind,
        string QuickHash) : TransferEvent;

    private sealed record Failed(string RelativePath) : TransferEvent;

    private sealed record Crashed(string RelativePath, Exception Exception) : TransferEvent;

    /// <summary>
    /// Owns everything the transfer phase mutates - counters, failed paths, crashes, the
    /// checkpoint clock and every callback invocation. Only the single recorder uses it.
    /// </summary>
    private sealed class TransferRecorder(
        ISnapshotRepository repository,
        long snapshotId,
        DateTimeOffset recordedAt,
        Counts counts,
        List<string> failedPaths,
        Action checkpointIfDue,
        CancellationTokenSource stopTransfers,
        Action<long>? onBytesTransferred,
        Action<string>? onFileStarted,
        Action<string>? onFileFinished,
        Action? onFileTransferred)
    {
        public List<Exception> Crashes { get; } = [];

        public void Record(TransferEvent transferEvent)
        {
            switch (transferEvent)
            {
                case FileStarted started:
                    onFileStarted?.Invoke(started.SourceAbsolutePath);
                    break;

                case BytesCopied copied:
                    onBytesTransferred?.Invoke(copied.Count);
                    break;

                case FileFinished finished:
                    onFileFinished?.Invoke(finished.SourceAbsolutePath);
                    break;

                case Transferred transferred:
                    repository.RecordFileVersion(
                        snapshotId, transferred.RelativePath, null, transferred.Hash, transferred.Size,
                        transferred.SourceModifiedAt, transferred.ChangeKind, recordedAt,
                        transferred.QuickHash, QuickHashPolicy.CurrentScheme);

                    counts.BytesTransferred += transferred.Size;
                    if (transferred.ChangeKind == FileChangeKind.Added) counts.Added++; else counts.Changed++;
                    checkpointIfDue();
                    onFileTransferred?.Invoke();
                    break;

                case Failed failed:
                    counts.Failed++;
                    failedPaths.Add(failed.RelativePath);
                    checkpointIfDue();
                    break;

                case Crashed crashed:
                    Crashes.Add(crashed.Exception);
                    stopTransfers.Cancel();
                    break;
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
