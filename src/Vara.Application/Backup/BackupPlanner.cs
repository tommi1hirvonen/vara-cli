using System.Collections.Concurrent;
using Vara.Core.Abstractions;
using Vara.Core.Hashing;
using Vara.Core.Snapshots;

namespace Vara.Application.Backup;

/// <summary>
/// Turns a diff result into a fully resolved <see cref="BackupPlan"/>: detects moves
/// (a newly-added path whose content hash matches a deleted path's known hash) so the
/// plan's total byte count reflects only genuine content transfers, known entirely
/// before execution begins.
///
/// Move-detection hashing runs in two phases: a concurrent signature-computation pass
/// (mirroring <see cref="BackupExecutor"/>'s transfer-stage parallelism) followed by
/// the same serial matching/consumption logic as before, now reading precomputed
/// signatures instead of hashing inline. Per candidate, a bounded-prefix "quick hash"
/// pre-filter (backup-execution spec's "Move detection avoids unbounded reads for
/// non-matching candidates" requirement) rules out a size-matched candidate from a
/// small bounded read whenever every still-possible candidate has a usable recorded
/// quick hash and none of them match; only then does a full-content read happen, and
/// only ever to confirm (never to declare) a match - see design.md.
/// </summary>
public sealed class BackupPlanner(IHasher hasher, int maxDegreeOfParallelism = 0)
{
    private readonly int _maxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;

    public BackupPlan Plan(DiffResult diff, IReadOnlyDictionary<string, CurrentFileState> currentState)
    {
        var operations = new List<PlannedOperation>();
        long totalBytes = 0;

        // Only "Added" entries (a path with no prior manifest row) are plausible move
        // targets - a "Changed" entry already existed at that path, so its new content
        // is a real edit, not a relocation. Group candidate deleted paths by size for a
        // cheap pre-filter before falling back to hashing.
        var deletedCandidatesBySize = diff.DeletedPaths
            .Where(currentState.ContainsKey)
            .Select(path => currentState[path])
            .GroupBy(state => state.Size)
            .ToDictionary(g => g.Key, g => g.ToList());

        var fullHashes = ComputeMoveCandidateFullHashes(diff.Pending, deletedCandidatesBySize);
        var consumedAsMoveSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in diff.Pending)
        {
            if (change.Kind == PendingChangeKind.Linked)
            {
                if (!change.Entry.TryGetValue(out ScannedLink link))
                {
                    throw new InvalidOperationException("A linked change must contain a scanned link.");
                }

                // Metadata-only, like Move/Delete below - never a move candidate (its
                // size is always 0 and it has no real content to hash), and no bytes to
                // transfer. LinkTarget carries the link's target path; KnownContentHash
                // stays null since there is no content-store hash for a link.
                // PreviousContentHash is set only for a file-to-link transition (the
                // path was not already tracked as a link), carrying the superseded
                // content's hash so the executor can remove it from the mirror - a Link
                // op otherwise performs no mirror I/O.
                var previousLinkContentHash = currentState.TryGetValue(link.RelativePath, out var previousLinkState) && !previousLinkState.IsLinked
                    ? previousLinkState.ContentHash
                    : null;
                operations.Add(new LinkOperation(link.RelativePath, previousLinkContentHash, link.LinkTarget));
                continue;
            }

            var file = GetScannedFile(change.Entry);
            CurrentFileState? moveMatch = null;
            if (change.Kind == PendingChangeKind.Added &&
                deletedCandidatesBySize.TryGetValue(file.Size, out var candidates) &&
                fullHashes.TryGetValue(file.RelativePath, out var entryHash) &&
                entryHash is not null)
            {
                moveMatch = FindMatch(entryHash, candidates, consumedAsMoveSource);
            }

            if (moveMatch is not null)
            {
                consumedAsMoveSource.Add(moveMatch.RelativePath);
                operations.Add(new MoveOperation(
                    file.RelativePath,
                    moveMatch.RelativePath,
                    moveMatch.ContentHash ?? throw new InvalidOperationException("A move match must have a content hash."),
                    file.Size,
                    file.ModifiedAt,
                    moveMatch.QuickHash,
                    moveMatch.QuickHashScheme));
                continue;
            }

            // For a Changed entry, the manifest's current-state view already holds the
            // previous content's hash (it's exactly how BackupDiffer classified this entry
            // as Changed rather than Added) - carried forward so PlaceAtMirrorPath can
            // restore read-only protection on that superseded blob after the overwrite
            // (protect-hardlinked-mirror-files change's design.md). Add has no previous
            // content at all, so this stays null for it.
            var previousContentHash = change.Kind == PendingChangeKind.Changed && currentState.TryGetValue(file.RelativePath, out var previousState)
                ? previousState.ContentHash
                : null;
            operations.Add(change.Kind switch
            {
                PendingChangeKind.Added => new AddOperation(file.RelativePath, file.AbsolutePath, file.Size, file.ModifiedAt),
                PendingChangeKind.Changed => new ChangeOperation(file.RelativePath, file.AbsolutePath, file.Size, file.ModifiedAt, previousContentHash),
                PendingChangeKind.Linked => throw new InvalidOperationException("A linked change must contain a scanned link."),
                _ => throw new ArgumentOutOfRangeException(nameof(change.Kind), change.Kind, "Unsupported pending change kind."),
            });
            totalBytes += file.Size;
        }

        foreach (var path in diff.DeletedPaths)
        {
            if (consumedAsMoveSource.Contains(path) || !currentState.TryGetValue(path, out var state))
            {
                continue;
            }

            operations.Add(new DeleteOperation(
                path, state.Size, state.SourceModifiedAt, state.ContentHash, state.QuickHash, state.QuickHashScheme));
        }

        return new BackupPlan(operations, totalBytes);
    }

    /// <summary>
    /// Computes, concurrently, the full content hash of every "Added" entry that
    /// shares its size with at least one deleted candidate - or determines that none
    /// of those candidates can match without needing a full read at all. Returns a map
    /// from relative path to either the confirmed full hash (to be matched serially
    /// against candidates, exactly as before) or <c>null</c> (no move is possible for
    /// this entry, whether because a bounded read has ruled every candidate out, or
    /// because the file could not be read at all).
    /// </summary>
    private ConcurrentDictionary<string, string?> ComputeMoveCandidateFullHashes(
        IReadOnlyList<PendingChange> pending, Dictionary<long, List<CurrentFileState>> deletedCandidatesBySize)
    {
        var results = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var sizeMatchedAdds = pending
            .Where(change => change.Kind == PendingChangeKind.Added && deletedCandidatesBySize.ContainsKey(GetScannedFile(change.Entry).Size))
            .ToList();

        Parallel.ForEach(
            sizeMatchedAdds,
            new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism },
            change =>
            {
                var file = GetScannedFile(change.Entry);
                var candidates = deletedCandidatesBySize[file.Size];
                results[file.RelativePath] = ComputeFullHashIfCandidateMatchPossible(file, candidates);
            });

        return results;
    }

    /// <summary>
    /// Reads <paramref name="entry"/>'s bounded-prefix quick hash first. If every
    /// candidate has a usable recorded quick hash (matching <see cref="QuickHashPolicy.CurrentScheme"/>)
    /// and none of them equals it, no candidate can possibly match - returns <c>null</c>
    /// without reading the rest of the file. Otherwise (a quick hash matched, or some
    /// candidate's signature is unusable and so cannot be cheaply ruled out) continues
    /// reading to compute and return the exact full hash, to be compared against
    /// candidates' full content hashes exactly as before.
    /// </summary>
    private string? ComputeFullHashIfCandidateMatchPossible(ScannedFile entry, List<CurrentFileState> candidates)
    {
        try
        {
            using var stream = File.OpenRead(entry.AbsolutePath);
            var signature = new StreamingContentSignature(stream, hasher);
            var quickHash = signature.ComputeQuickHash();

            var everyCandidateUsable = true;
            var quickHashMatchesAny = false;
            foreach (var candidate in candidates)
            {
                if (candidate.QuickHashScheme != QuickHashPolicy.CurrentScheme || candidate.QuickHash is null)
                {
                    // Recorded signature unavailable for this candidate - it cannot be
                    // cheaply ruled out, so a full read is required regardless of what
                    // the other candidates' quick hashes say.
                    everyCandidateUsable = false;
                    continue;
                }

                if (candidate.QuickHash == quickHash)
                {
                    quickHashMatchesAny = true;
                }
            }

            if (everyCandidateUsable && !quickHashMatchesAny)
            {
                // Every candidate had a usable signature and none matched - no candidate
                // can possibly match; no further reading needed.
                return null;
            }

            return signature.ContinueToFullHash();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't read the file during this pre-check (e.g. locked) - fall back to
            // treating it as a normal add; the execute stage's own read attempt will
            // surface and record the real failure.
            return null;
        }
    }

    private static ScannedFile GetScannedFile(ScanItem item)
    {
        if (!item.TryGetValue(out ScannedFile file))
        {
            throw new InvalidOperationException("A file change must contain a scanned file.");
        }

        return file;
    }

    private static CurrentFileState? FindMatch(string entryHash, List<CurrentFileState> candidates, HashSet<string> consumed)
    {
        foreach (var candidate in candidates)
        {
            if (!consumed.Contains(candidate.RelativePath) && entryHash == candidate.ContentHash)
            {
                return candidate;
            }
        }

        return null;
    }
}
