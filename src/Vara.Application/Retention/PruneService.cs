using Vara.Core.Abstractions;
using Vara.Core.Backup;
using Vara.Core.Configuration;

namespace Vara.Application.Retention;

/// <summary>The outcome of a prune run.</summary>
public sealed record PruneResult(int SnapshotsRemoved, int BlobsRemoved);

/// <summary>
/// Reports progress of <see cref="PruneService.Prune"/>'s blob-deletion (garbage
/// collection) phase only - the earlier DB/diff steps report nothing, since their
/// duration isn't practically predictable upfront. See the progress-reporting and
/// retention-pruning specs in the add-prune-progress-reporting change.
/// </summary>
public sealed record PruneProgress(int BlobsDeleted, int TotalBlobs);

/// <summary>
/// Applies a profile's tiered retention policy (removing expired snapshot records)
/// and then garbage-collects version-store content no longer referenced by any
/// remaining snapshot or the live mirror. See the retention-pruning spec.
/// </summary>
public sealed class PruneService(ISnapshotRepository repository, IContentStore contentStore, IRunLock runLock)
{
    private readonly RetentionEvaluator _evaluator = new();

    /// <summary>
    /// Read-only preview of how many snapshots <see cref="Prune"/> would remove, without
    /// acquiring the run lock or deleting anything. Meant to be called before <see cref="Prune"/>
    /// so a caller (the CLI) can decide whether to prompt for confirmation. Because no lock is
    /// held between this call and a subsequent <see cref="Prune"/> call, the count can be stale
    /// by the time <see cref="Prune"/> actually runs - for example if a concurrent backup adds a
    /// new snapshot in between. See design.md's "Preview/execute race" risk in the
    /// <c>confirm-prune</c> change.
    /// </summary>
    /// <exception cref="RetentionPolicyNotConfiguredException">The profile has no retention policy configured.</exception>
    public int CountEligibleForRemoval(Profile profile)
    {
        if (profile.Retention is null)
        {
            throw new RetentionPolicyNotConfiguredException(profile.Name);
        }

        var snapshots = repository.ListSnapshots();
        return _evaluator.DetermineEligibleForRemoval(snapshots, profile.Retention).Count;
    }

    /// <exception cref="RetentionPolicyNotConfiguredException">The profile has no retention policy configured.</exception>
    /// <exception cref="PruneAlreadyRunningException">Another run (backup or prune) is already in progress for this profile.</exception>
    public PruneResult Prune(Profile profile, IProgress<PruneProgress>? progress = null)
    {
        if (profile.Retention is null)
        {
            throw new RetentionPolicyNotConfiguredException(profile.Name);
        }

        using (runLock)
        {
            if (!runLock.TryAcquire(profile.Name, profile.TargetRoot))
            {
                throw new PruneAlreadyRunningException(profile.Name);
            }

            var snapshots = repository.ListSnapshots();
            var eligible = _evaluator.DetermineEligibleForRemoval(snapshots, profile.Retention);
            var eligibleIds = eligible.Select(s => s.Id).ToList();

            var snapshotsRemoved = repository.PruneSnapshots(eligibleIds);

            // GC: any blob physically in the store that is no longer referenced by any
            // remaining manifest row (current or historical) is now garbage. The live
            // mirror is untouched throughout - pruning only ever deletes manifest rows
            // and version-store blobs, never mirror files.
            var referencedHashes = repository.GetAllReferencedContentHashes();
            var unreferencedHashes = contentStore.ListAllStoredHashes().Except(referencedHashes).ToList();

            if (unreferencedHashes.Count > 0)
            {
                progress?.Report(new PruneProgress(0, unreferencedHashes.Count));
            }

            for (var i = 0; i < unreferencedHashes.Count; i++)
            {
                contentStore.DeleteContent(unreferencedHashes[i]);
                progress?.Report(new PruneProgress(i + 1, unreferencedHashes.Count));
            }

            return new PruneResult(snapshotsRemoved, unreferencedHashes.Count);
        }
    }
}
