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

            try
            {
                var currentState = repository.GetCurrentState();
                var scanned = scanner.Scan(profile.Sources);
                var diff = new BackupDiffer().Diff(scanned, currentState);
                var plan = new BackupPlanner(hasher).Plan(diff, currentState);

                progress?.Report(new BackupProgress(0, plan.TotalBytesToTransfer));

                var bytesSoFar = 0L;
                var outcome = new BackupExecutor(contentStore, repository).Execute(snapshotId, startedAt, plan, transferred =>
                {
                    bytesSoFar += transferred;
                    progress?.Report(new BackupProgress(bytesSoFar, plan.TotalBytesToTransfer));
                });

                var stats = new SnapshotStats(
                    outcome.BytesTransferred, outcome.FilesAdded, outcome.FilesChanged, outcome.FilesMoved, outcome.FilesDeleted, outcome.FilesFailed);
                var completedAt = DateTimeOffset.UtcNow;
                repository.CompleteSnapshot(snapshotId, completedAt, stats);

                return new BackupRunResult(snapshotId, startedAt, completedAt, stats, outcome.FailedPaths);
            }
            catch
            {
                repository.FailSnapshot(snapshotId, DateTimeOffset.UtcNow, SnapshotStats.Empty);
                throw;
            }
        }
    }
}
