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
    /// Read-only preview of which snapshots <see cref="Prune"/> would remove, without
    /// acquiring the run lock or deleting anything. Meant to be called before <see cref="Prune"/>
    /// so a caller (the CLI) can decide whether to prompt for confirmation, and - if it does -
    /// pass the returned ids back in as <see cref="Prune"/>'s <c>confirmedSnapshotIds</c> so only
    /// what was actually shown to the user can be removed. Because no lock is held between this
    /// call and a subsequent <see cref="Prune"/> call, the returned set can be stale by the time
    /// <see cref="Prune"/> actually runs - for example if a concurrent backup adds a new snapshot
    /// in between, or removes one of the returned ids by some other means. <see cref="Prune"/>
    /// re-evaluates eligibility itself and only ever removes ids that are both confirmed and
    /// still live-eligible, so a stale preview can only cause it to remove fewer snapshots than
    /// shown, never more or different ones. See design.md's "Preview/execute race" risk in the
    /// <c>confirm-prune</c> change and this change's design.md.
    /// </summary>
    /// <exception cref="RetentionPolicyNotConfiguredException">The profile has no retention policy configured.</exception>
    public IReadOnlyList<long> ListEligibleForRemoval(Profile profile)
    {
        if (profile.Retention is null)
        {
            throw new RetentionPolicyNotConfiguredException(profile.Name);
        }

        var snapshots = repository.ListSnapshots();
        return _evaluator.DetermineEligibleForRemoval(snapshots, profile.Retention).Select(s => s.Id).ToList();
    }

    /// <summary>
    /// Thin wrapper around <see cref="ListEligibleForRemoval"/> for callers that only need the
    /// count (e.g. for a confirmation prompt) and don't need to hold onto the concrete id set.
    /// </summary>
    /// <exception cref="RetentionPolicyNotConfiguredException">The profile has no retention policy configured.</exception>
    public int CountEligibleForRemoval(Profile profile) => ListEligibleForRemoval(profile).Count;

    /// <param name="confirmedSnapshotIds">
    /// The specific snapshot ids a caller previously showed to and had confirmed by the user
    /// (typically via <see cref="ListEligibleForRemoval"/>). When given, only the intersection of
    /// these ids and the live eligible set is removed - a snapshot that becomes eligible only
    /// after the confirmed set was captured is left alone for a subsequent prune run to pick up,
    /// and a confirmed id no longer present or no longer eligible is silently skipped rather than
    /// treated as an error. When omitted (the default), the full live eligible set is removed,
    /// exactly as before this parameter existed - used for the <c>--yes</c> and zero-eligible
    /// paths, where nothing specific was ever confirmed.
    /// </param>
    /// <exception cref="RetentionPolicyNotConfiguredException">The profile has no retention policy configured.</exception>
    /// <exception cref="PruneAlreadyRunningException">Another run (backup or prune) is already in progress for this profile.</exception>
    public PruneResult Prune(Profile profile, IProgress<PruneProgress>? progress = null, IReadOnlyList<long>? confirmedSnapshotIds = null)
    {
        if (profile.Retention is null)
        {
            throw new RetentionPolicyNotConfiguredException(profile.Name);
        }

        using (runLock)
        {
            if (!runLock.TryAcquire(profile.Name, profile.TargetRoot))
            {
                throw new PruneAlreadyRunningException(profile.Name, profile.TargetRoot);
            }

            var snapshots = repository.ListSnapshots();
            var eligible = _evaluator.DetermineEligibleForRemoval(snapshots, profile.Retention);
            var eligibleIds = eligible.Select(s => s.Id).ToList();

            if (confirmedSnapshotIds is not null)
            {
                var confirmedSet = confirmedSnapshotIds.ToHashSet();
                eligibleIds = eligibleIds.Where(confirmedSet.Contains).ToList();
            }

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
