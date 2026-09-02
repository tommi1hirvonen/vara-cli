using Vara.Core.Abstractions;
using Vara.Core.Backup;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;

namespace Vara.Application.Backup;

/// <summary>
/// Orchestrates a full backup run for one profile: acquire the run lock, reconcile any
/// incomplete snapshot from a previous interrupted run, probe hardlink support, then
/// scan -> diff -> plan -> execute -> commit. See design.md's "Backup pipeline stages".
/// </summary>
public sealed class BackupPipeline(
    IFileSystemScanner scanner,
    IHasher hasher,
    IContentStore contentStore,
    ISnapshotRepository repository,
    IRunLock runLock)
{
    public BackupRunResult Run(Profile profile, IProgress<BackupProgress>? progress = null)
    {
        using (runLock)
        {
            if (!runLock.TryAcquire(profile.Name, profile.TargetRoot))
            {
                throw new BackupAlreadyRunningException(profile.Name);
            }

            repository.ReconcileIncompleteSnapshots();
            contentStore.ProbeHardlinkSupport();
            contentStore.CleanupOrphanedTemp();

            var startedAt = DateTimeOffset.UtcNow;
            var snapshotId = repository.BeginSnapshot(startedAt);

            // BeginSnapshot above commits immediately (outside the batch) so an
            // interrupted run is still discoverable as Running on next startup. Everything
            // from here on - the executor's per-file manifest writes plus the trailing
            // outcome update - is grouped into one batch commit per the
            // "Manifest writes are batched per snapshot" requirement, instead of paying a
            // durable commit for every file. A crash before Commit() rolls the whole batch
            // back; ReconcileIncompleteSnapshots then finds the snapshot still Running on
            // the next startup, and the next run's incremental scan re-detects and
            // re-records any affected paths (self-healing, not data loss - see design.md).
            using var manifestBatch = repository.BeginManifestBatch();

            try
            {
                var currentState = repository.GetCurrentState();
                var scanResult = scanner.Scan(profile.Sources);
                var diff = new BackupDiffer().Diff(scanResult.Entries, currentState);
                var plan = new BackupPlanner(hasher, profile.Concurrency?.ScanConcurrency ?? 0).Plan(diff, currentState);

                // The live progress denominator can differ from plan.TotalBytesToTransfer
                // (which keeps its plain "total changed source bytes" meaning everywhere
                // else): when the target doesn't support hardlinks at all, every Add/Change
                // operation's PlaceAtMirrorPath call is known upfront to re-stream its
                // content as a fallback copy in addition to the source-read pass, so the
                // denominator is doubled to keep the reported percentage from exceeding
                // 100% over the course of the run (stream-large-file-transfer-progress
                // change's design.md - "Avoiding double-counting when a mirror placement
                // also streams"). The rare remaining case - hardlinks supported at the
                // volume level but a specific blob's own link limit reached - isn't knowable
                // upfront and can still cause a small, transient overshoot; accepted as a
                // bounded edge case.
                var progressTotalBytes = contentStore.SupportsHardlinks ? plan.TotalBytesToTransfer : plan.TotalBytesToTransfer * 2;

                // bytesSoFar is updated from chunk-level callbacks that may arrive
                // concurrently across multiple in-flight file transfers once
                // transfer_concurrency is configured above its default of 1 - Interlocked
                // keeps it correct without taking BackupExecutor's own per-file reportLock
                // (which continues to serialize only the once-per-file manifest write and
                // summary counter, independent of this live-progress counter).
                var bytesSoFar = 0L;

                // The first progress report - which seeds the live display's
                // throughput/ETA clock (BackupProgressCalculator lazily starts its own
                // elapsed-time clock on its first Calculate() call, itself driven by this
                // first report) - is deliberately deferred until Execute's Move/Delete pass
                // has finished, rather than emitted up front. Otherwise, that pass's own
                // wall-clock time (mirror renames/removals, manifest writes - reporting
                // zero bytes) would be folded into the throughput denominator as if it
                // were part of the byte-transfer phase, permanently skewing the run's
                // reported throughput and ETA (fix-transfer-clock-start change's design.md).
                var outcome = new BackupExecutor(contentStore, repository, hasher, profile.Concurrency?.TransferConcurrency ?? 0).Execute(
                    snapshotId,
                    startedAt,
                    plan,
                    transferred =>
                    {
                        var total = Interlocked.Add(ref bytesSoFar, transferred);
                        progress?.Report(new BackupProgress(total, progressTotalBytes));
                    },
                    onTransferPhaseStarting: () => progress?.Report(new BackupProgress(0, progressTotalBytes)));

                // BackupDiffer.Diff above fully enumerates scanResult.Entries, so
                // scanResult.Failures is guaranteed complete by this point. Scan-time
                // failures join the executor's per-file failures in the same run
                // result, per the backup-execution spec's "Unreadable files do not
                // abort the run" requirement.
                var failedPaths = scanResult.Failures.Select(f => f.RelativePath).Concat(outcome.FailedPaths).ToList();
                var stats = new SnapshotStats(
                    outcome.BytesTransferred, outcome.FilesAdded, outcome.FilesChanged, outcome.FilesMoved, outcome.FilesDeleted,
                    outcome.FilesFailed + scanResult.Failures.Count);
                var completedAt = DateTimeOffset.UtcNow;
                repository.CompleteSnapshot(snapshotId, completedAt, stats);
                manifestBatch.Commit();

                return new BackupRunResult(snapshotId, startedAt, completedAt, stats, failedPaths);
            }
            catch
            {
                // A handled failure (unlike a real process crash) still commits: the
                // snapshot's Failed status and whatever rows were recorded should persist
                // rather than vanish, so history/listing reflect the failed run instead of
                // relying on ReconcileIncompleteSnapshots to notice it next startup.
                repository.FailSnapshot(snapshotId, DateTimeOffset.UtcNow, SnapshotStats.Empty);
                manifestBatch.Commit();
                throw;
            }
        }
    }
}
