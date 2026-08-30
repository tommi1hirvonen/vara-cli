using Vara.Core.Abstractions;
using Vara.Core.Backup;
using Vara.Core.Configuration;

namespace Vara.Application.Retention;

/// <summary>The outcome of a prune run.</summary>
public sealed record PruneResult(int SnapshotsRemoved, int BlobsRemoved);

/// <summary>
/// Applies a profile's tiered retention policy (removing expired snapshot records)
/// and then garbage-collects version-store content no longer referenced by any
/// remaining snapshot or the live mirror. See the retention-pruning spec.
/// </summary>
public sealed class PruneService(ISnapshotRepository repository, IContentStore contentStore, IRunLock runLock)
{
    private readonly RetentionEvaluator _evaluator = new();

    /// <exception cref="RetentionPolicyNotConfiguredException">The profile has no retention policy configured.</exception>
    /// <exception cref="PruneAlreadyRunningException">Another run (backup or prune) is already in progress for this profile.</exception>
    public PruneResult Prune(Profile profile)
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

            foreach (var hash in unreferencedHashes)
            {
                contentStore.DeleteContent(hash);
            }

            return new PruneResult(snapshotsRemoved, unreferencedHashes.Count);
        }
    }
}
