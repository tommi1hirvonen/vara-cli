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
/// concurrent use from multiple threads. The caller (<see cref="BackupPipeline"/>)
/// wraps a whole snapshot's manifest writes - the ones made here plus its own
/// trailing outcome update - in one <see cref="Vara.Core.Abstractions.IManifestBatch"/>
/// commit, so this executor pays no per-file commit cost even though it still calls
/// <see cref="Vara.Core.Abstractions.ISnapshotRepository.RecordFileVersion"/> once per
/// operation (batch-manifest-writes change's design.md).
/// </summary>
public sealed class BackupExecutor(IContentStore contentStore, ISnapshotRepository repository, IHasher hasher, int maxDegreeOfParallelism = 0)
{
    // The most common target is a slower external drive (e.g. an HDD), which performs
    // best when written to by one stream at a time rather than several interleaved
    // ones; the scan stage's own default (Environment.ProcessorCount) is unaffected,
    // since it only reads the source and never touches the target. See design.md of
    // the configure-backup-concurrency change - kept as a single named constant so the
    // default can be revised later without touching call sites.
    private const int DefaultTransferConcurrency = 1;

    private readonly int _maxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : DefaultTransferConcurrency;

    public ExecutionOutcome Execute(
        long snapshotId,
        DateTimeOffset recordedAt,
        BackupPlan plan,
        Action<long>? onBytesTransferred = null,
        Action? onTransferPhaseStarting = null)
    {
        var counts = new Counts();
        var failedPaths = new List<string>();
        var reportLock = new object();

        foreach (var operation in plan.Operations.Where(o => o.Kind is PlannedOperationKind.Move or PlannedOperationKind.Delete))
        {
            ExecuteMoveOrDelete(operation, snapshotId, recordedAt, counts, failedPaths, reportLock);
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
            operation => ExecuteTransfer(operation, snapshotId, recordedAt, counts, failedPaths, reportLock, onBytesTransferred));

        return new ExecutionOutcome(counts.BytesTransferred, counts.Added, counts.Changed, counts.Moved, counts.Deleted, counts.Failed, failedPaths);
    }

    private void ExecuteMoveOrDelete(PlannedOperation operation, long snapshotId, DateTimeOffset recordedAt, Counts counts, List<string> failedPaths, object reportLock)
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
            else
            {
                contentStore.RemoveFromMirror(operation.RelativePath, operation.KnownContentHash!);
                repository.RecordFileVersion(
                    snapshotId, operation.RelativePath, null, operation.KnownContentHash!,
                    operation.Size, operation.SourceModifiedAt, FileChangeKind.Deleted, recordedAt,
                    operation.QuickHash, operation.QuickHashScheme);
                counts.Deleted++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (reportLock)
            {
                counts.Failed++;
                failedPaths.Add(operation.RelativePath);
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
        Action<long>? onBytesTransferred)
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

            contentStore.PlaceAtMirrorPath(hash, operation.RelativePath, onBytesTransferred, operation.PreviousContentHash);

            lock (reportLock)
            {
                var changeKind = operation.Kind == PlannedOperationKind.Add ? FileChangeKind.Added : FileChangeKind.Changed;
                repository.RecordFileVersion(
                    snapshotId, operation.RelativePath, null, hash, size, operation.SourceModifiedAt, changeKind, recordedAt,
                    quickHash, QuickHashPolicy.CurrentScheme);

                counts.BytesTransferred += size;
                if (changeKind == FileChangeKind.Added) counts.Added++; else counts.Changed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (reportLock)
            {
                counts.Failed++;
                failedPaths.Add(operation.RelativePath);
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
    }
}
