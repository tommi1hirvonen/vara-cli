using System.Threading;
using Vara.Core.Abstractions;

namespace Vara.Application.Integrity;

/// <summary>
/// One missing or corrupt content blob, with the file path(s) that reference it so a
/// finding is actionable rather than just an opaque hash. <see cref="AffectedPaths"/>
/// is capped at a small number per finding (see design.md's "capping the listed paths
/// per hash" risk mitigation for heavily-deduplicated content) - <see cref="TotalAffectedPathCount"/>
/// is the true, uncapped count, so a caller can show a "+N more" summary when the list
/// was truncated.
/// </summary>
public sealed record IntegrityFinding(string ContentHash, IReadOnlyList<string> AffectedPaths, int TotalAffectedPathCount);

/// <summary>The outcome of an <see cref="IntegrityCheckService.Check"/> run.</summary>
/// <param name="BlobsChecked">Total number of content blobs referenced by the manifest that were examined.</param>
/// <param name="Missing">Referenced blobs not physically present in the target's content store.</param>
/// <param name="Corrupt">Referenced blobs present in the store but whose re-hashed content no longer matches the manifest. Always empty in quick mode, since quick mode never re-hashes.</param>
/// <param name="Orphaned">Blobs physically present in the store but not referenced by any snapshot - informational only; <see cref="IntegrityCheckService.Check"/> never deletes these (that remains <c>vara prune</c>'s job).</param>
/// <param name="Cancelled">
/// <see langword="true"/> when a graceful Ctrl+C stop was requested before verification of every
/// referenced blob completed, per the backup-integrity spec's "Graceful cancellation via Ctrl+C"
/// requirement. <see cref="BlobsChecked"/> and the finding lists reflect only what was actually
/// verified before the stop - already-found problems are not discarded on cancellation.
/// </param>
public sealed record IntegrityCheckResult(
    int BlobsChecked,
    IReadOnlyList<IntegrityFinding> Missing,
    IReadOnlyList<IntegrityFinding> Corrupt,
    IReadOnlyList<string> Orphaned,
    bool Cancelled = false);

/// <summary>
/// Reports progress of <see cref="IntegrityCheckService.Check"/>'s blob-verification
/// pass (blobs checked so far / total referenced blobs), consistent with
/// <c>PruneProgress</c>'s shape.
/// </summary>
public sealed record IntegrityCheckProgress(int BlobsChecked, int TotalBlobs);

/// <summary>
/// Verifies that content physically stored in a profile's target still matches what
/// the manifest expects, across the full referenced snapshot history (not only the
/// current live mirror) - see the backup-integrity spec. Strictly read-only: never
/// modifies the target, the manifest, or any stored content.
/// </summary>
public sealed class IntegrityCheckService(ISnapshotRepository repository, IContentStore contentStore, IHasher hasher)
{
    /// <summary>
    /// Cap on the number of affected paths attached per finding - see design.md's
    /// "capping the listed paths per hash" risk mitigation for content shared across
    /// many files/versions.
    /// </summary>
    private const int MaxAffectedPathsPerFinding = 5;

    /// <param name="quick">
    /// When <see langword="true"/>, only checks that each referenced blob is physically
    /// present in the store, without reading its content or recomputing its hash -
    /// trading full corruption detection for a faster presence-only check.
    /// </param>
    /// <param name="cancellationToken">
    /// Cooperative "stop starting new work" signal, set by a graceful Ctrl+C stop (see
    /// <c>Vara.Cli.Program</c>'s <c>Console.CancelKeyPress</c> handler), mirroring
    /// <see cref="Vara.Application.Backup.BackupPipeline.Run"/>'s own parameter. Checked between
    /// each referenced blob verified; whatever was already verified is reported normally - see
    /// the backup-integrity spec's "Graceful cancellation via Ctrl+C" requirement.
    /// </param>
    public IntegrityCheckResult Check(bool quick, IProgress<IntegrityCheckProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var referencedHashes = repository.GetAllReferencedContentHashes().ToList();
        var storedHashes = contentStore.ListAllStoredHashes();

        var missing = new List<IntegrityFinding>();
        var corrupt = new List<IntegrityFinding>();

        if (referencedHashes.Count > 0)
        {
            progress?.Report(new IntegrityCheckProgress(0, referencedHashes.Count));
        }

        var blobsChecked = 0;
        var cancelled = false;
        for (var i = 0; i < referencedHashes.Count; i++)
        {
            // Cooperative cancellation: stop starting verification of any further blob once a
            // graceful Ctrl+C stop has been requested. Whatever has already been verified is
            // allowed to finish (this loop is sequential, so nothing is "in flight" here beyond
            // the current iteration) - mirroring BackupExecutor's own cancellation checks.
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var hash = referencedHashes[i];

            if (!storedHashes.Contains(hash))
            {
                missing.Add(BuildFinding(hash));
            }
            else if (!quick)
            {
                using var content = contentStore.OpenRead(hash);
                var actualHash = hasher.ComputeHash(content);
                if (!string.Equals(actualHash, hash, StringComparison.Ordinal))
                {
                    corrupt.Add(BuildFinding(hash));
                }
            }

            blobsChecked = i + 1;
            progress?.Report(new IntegrityCheckProgress(blobsChecked, referencedHashes.Count));
        }

        var orphaned = storedHashes.Except(referencedHashes).ToList();

        return new IntegrityCheckResult(blobsChecked, missing, corrupt, orphaned, Cancelled: cancelled);
    }

    private IntegrityFinding BuildFinding(string hash)
    {
        var paths = repository.GetPathsForContentHash(hash);
        return new IntegrityFinding(hash, paths.Take(MaxAffectedPathsPerFinding).ToList(), paths.Count);
    }
}
