using Vara.Core.Abstractions;
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
/// concurrent use from multiple threads.
/// </summary>
public sealed class BackupExecutor(IContentStore contentStore, ISnapshotRepository repository, int maxDegreeOfParallelism = 0)
{
    private readonly int _maxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;

    public ExecutionOutcome Execute(long snapshotId, DateTimeOffset recordedAt, BackupPlan plan, Action<long>? onBytesTransferred = null)
    {
        var counts = new Counts();
        var failedPaths = new List<string>();
        var reportLock = new object();

        foreach (var operation in plan.Operations.Where(o => o.Kind is PlannedOperationKind.Move or PlannedOperationKind.Delete))
        {
            ExecuteMoveOrDelete(operation, snapshotId, recordedAt, counts, failedPaths, reportLock);
        }

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
                    operation.Size, operation.SourceModifiedAt, FileChangeKind.Deleted, recordedAt);
                repository.RecordFileVersion(
                    snapshotId, operation.RelativePath, operation.PreviousRelativePath, operation.KnownContentHash!,
                    operation.Size, operation.SourceModifiedAt, FileChangeKind.Moved, recordedAt);
                counts.Moved++;
            }
            else
            {
                contentStore.RemoveFromMirror(operation.RelativePath);
                repository.RecordFileVersion(
                    snapshotId, operation.RelativePath, null, operation.KnownContentHash!,
                    operation.Size, operation.SourceModifiedAt, FileChangeKind.Deleted, recordedAt);
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

            // The actual disk I/O - reading the source and streaming it into the
            // content store - happens outside the lock so it can run concurrently
            // across threads.
            using (var sourceStream = File.OpenRead(operation.SourceAbsolutePath!))
            {
                (hash, size) = contentStore.StoreFromStream(sourceStream);
            }

            contentStore.PlaceAtMirrorPath(hash, operation.RelativePath);

            lock (reportLock)
            {
                var changeKind = operation.Kind == PlannedOperationKind.Add ? FileChangeKind.Added : FileChangeKind.Changed;
                repository.RecordFileVersion(snapshotId, operation.RelativePath, null, hash, size, operation.SourceModifiedAt, changeKind, recordedAt);

                counts.BytesTransferred += size;
                if (changeKind == FileChangeKind.Added) counts.Added++; else counts.Changed++;
                onBytesTransferred?.Invoke(size);
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
