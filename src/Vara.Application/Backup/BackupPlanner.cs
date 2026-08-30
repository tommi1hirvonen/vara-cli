using Vara.Core.Abstractions;
using Vara.Core.Snapshots;

namespace Vara.Application.Backup;

/// <summary>
/// Turns a diff result into a fully resolved <see cref="BackupPlan"/>: detects moves
/// (a newly-added path whose content hash matches a deleted path's known hash) so the
/// plan's total byte count reflects only genuine content transfers, known entirely
/// before execution begins.
/// </summary>
public sealed class BackupPlanner(IHasher hasher)
{
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
        var consumedAsMoveSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in diff.Pending)
        {
            CurrentFileState? moveMatch = null;
            if (change.Kind == PendingChangeKind.Added &&
                deletedCandidatesBySize.TryGetValue(change.Entry.Size, out var candidates))
            {
                moveMatch = TryFindMoveMatch(change.Entry, candidates, consumedAsMoveSource);
            }

            if (moveMatch is not null)
            {
                consumedAsMoveSource.Add(moveMatch.RelativePath);
                operations.Add(new PlannedOperation(
                    PlannedOperationKind.Move,
                    change.Entry.RelativePath,
                    moveMatch.RelativePath,
                    change.Entry.AbsolutePath,
                    change.Entry.Size,
                    change.Entry.ModifiedAt,
                    moveMatch.ContentHash));
                continue;
            }

            var kind = change.Kind == PendingChangeKind.Added ? PlannedOperationKind.Add : PlannedOperationKind.Change;
            operations.Add(new PlannedOperation(
                kind, change.Entry.RelativePath, null, change.Entry.AbsolutePath, change.Entry.Size, change.Entry.ModifiedAt, null));
            totalBytes += change.Entry.Size;
        }

        foreach (var path in diff.DeletedPaths)
        {
            if (consumedAsMoveSource.Contains(path) || !currentState.TryGetValue(path, out var state))
            {
                continue;
            }

            operations.Add(new PlannedOperation(
                PlannedOperationKind.Delete, path, null, null, state.Size, state.SourceModifiedAt, state.ContentHash));
        }

        return new BackupPlan(operations, totalBytes);
    }

    private CurrentFileState? TryFindMoveMatch(ScannedEntry entry, List<CurrentFileState> candidates, HashSet<string> consumed)
    {
        string? entryHash = null;

        foreach (var candidate in candidates)
        {
            if (consumed.Contains(candidate.RelativePath))
            {
                continue;
            }

            entryHash ??= TryHashFile(entry.AbsolutePath);
            if (entryHash is null)
            {
                // Couldn't read the file during this pre-check (e.g. locked) - fall back
                // to treating it as a normal add; the execute stage's own read attempt
                // will surface and record the real failure.
                return null;
            }

            if (entryHash == candidate.ContentHash)
            {
                return candidate;
            }
        }

        return null;
    }

    private string? TryHashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return hasher.ComputeHash(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
