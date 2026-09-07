using System.Threading;
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
    IRunLock runLock,
    TextWriter? diagnostics = null)
{
    // Defaults to real standard error - the same physical stream Vara.Cli's
    // StandardError.Console writes user-facing hard errors to - so a secondary
    // failure recorded here (see the catch block below) remains observable outside
    // of tests without this layer needing a dependency on Vara.Cli/Spectre.
    // Overridable so tests can assert against an in-memory writer instead.
    private readonly TextWriter _diagnostics = diagnostics ?? Console.Error;


    /// <param name="cancellationToken">
    /// Cooperative "stop starting new work" signal, set by a graceful Ctrl+C stop (see
    /// <c>Vara.Cli.Program</c>'s <c>Console.CancelKeyPress</c> handler). When requested,
    /// <see cref="BackupExecutor"/> finishes whatever operations are already in flight
    /// and starts no new ones; this method then forces an immediate checkpoint covering
    /// everything completed so far and records the snapshot as <see cref="SnapshotStatus.Cancelled"/>
    /// instead of <see cref="SnapshotStatus.Complete"/> - see backup-execution's
    /// "Graceful cancellation via Ctrl+C" requirement and design.md.
    /// </param>
    public BackupRunResult Run(Profile profile, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using (runLock)
        {
            if (!runLock.TryAcquire(profile.Name, profile.TargetRoot))
            {
                throw new BackupAlreadyRunningException(profile.Name, profile.TargetRoot);
            }

            repository.ReconcileIncompleteSnapshots();
            contentStore.ProbeHardlinkSupport();
            contentStore.CleanupOrphanedTemp();

            var startedAt = DateTimeOffset.UtcNow;
            var snapshotId = repository.BeginSnapshot(startedAt);

            // BeginSnapshot above commits immediately (outside the batch) so an
            // interrupted run is still discoverable as Running on next startup. Everything
            // from here on - the executor's per-file manifest writes plus the trailing
            // outcome update - is grouped into periodic checkpoint commits per the
            // "Manifest writes are checkpointed periodically during a run" requirement,
            // instead of paying a durable commit for every file, or deferring to a single
            // commit for the whole run. A crash between checkpoints rolls back only the
            // writes made since the most recent one; ReconcileIncompleteSnapshots then
            // finds the snapshot still Running on the next startup, and the next run's
            // incremental scan re-detects and re-records any affected paths (self-healing,
            // not data loss - see design.md).
            using var manifestBatch = repository.BeginManifestBatch();

            try
            {
                var currentState = repository.GetCurrentState();
                var scanResult = scanner.Scan(profile.Sources);
                var diff = new BackupDiffer().Diff(scanResult.Entries, currentState, scanResult.Failures);
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
                    onTransferPhaseStarting: () => progress?.Report(new BackupProgress(0, progressTotalBytes)),
                    manifestBatch: manifestBatch,
                    cancellationToken: cancellationToken);

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

                if (cancellationToken.IsCancellationRequested)
                {
                    // Graceful stop: the executor above finished in-flight operations and
                    // started no new ones. Recording Cancelled (rather than Complete) here
                    // and forcing a checkpoint commit are both done before returning, so a
                    // single Ctrl+C never leaves the snapshot Running for
                    // ReconcileIncompleteSnapshots to later mark Failed instead.
                    repository.CancelSnapshot(snapshotId, completedAt, stats);
                    manifestBatch.Commit();
                    return new BackupRunResult(snapshotId, startedAt, completedAt, stats, failedPaths, Cancelled: true);
                }

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
                //
                // Failure recording is isolated in its own try/catch so that a secondary
                // exception here (e.g. FailSnapshot or Commit itself throwing) can never
                // replace the original exception being handled by this catch block - the
                // bare `throw;` below always rethrows that original exception, regardless
                // of whether recording succeeded. The secondary exception is not silently
                // discarded: it is surfaced via `_diagnostics` so it stays observable
                // even though it does not become the exception the caller sees.
                try
                {
                    repository.FailSnapshot(snapshotId, DateTimeOffset.UtcNow, SnapshotStats.Empty);
                    manifestBatch.Commit();
                }
                catch (Exception recordingException)
                {
                    _diagnostics.WriteLine(
                        $"Failed to record snapshot {snapshotId}'s failure state: {recordingException}");
                }

                throw;
            }
        }
    }
}
