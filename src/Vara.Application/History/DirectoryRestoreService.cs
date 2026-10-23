using Vara.Core.Abstractions;
using Vara.Core.FileSystem;
using Vara.Core.Snapshots;

namespace Vara.Application.History;

/// <summary>
/// Restoring a whole directory tree to a point in time - choosing the target snapshot, planning
/// every write/removal up front, then executing the plan - per the snapshot-history spec's
/// directory-restore requirements. One instance is scoped to one profile's manifest and
/// content store.
/// </summary>
public sealed class DirectoryRestoreService(ISnapshotRepository repository, IContentStore contentStore)
{
    /// <summary>
    /// Computes the full write/remove plan for restoring every tracked path under
    /// <paramref name="directoryPath"/> to its state as of <paramref name="asOf"/> (or the
    /// current state, if <c>null</c>), per the snapshot-history capability's "Restore a
    /// directory at a given date" requirement. Every path tracked as live at that date is
    /// planned for writing (<see cref="DirectoryRestorePlan.ToWrite"/>), including one later
    /// deleted from the profile; a path currently tracked as live under the directory but not
    /// live at that date is planned for removal (<see cref="DirectoryRestorePlan.ToRemove"/>)
    /// if it currently exists at its computed destination. Every computed destination is
    /// validated against the live mirror before this method returns, so a violation aborts the
    /// whole plan rather than causing a partial restore.
    /// </summary>
    /// <param name="outRoot">
    /// The destination directory each write is placed under, relative to
    /// <paramref name="directoryPath"/>'s own prefix. Required (and used) unless
    /// <paramref name="inPlace"/> is <c>true</c>.
    /// </param>
    /// <param name="inPlace">
    /// When <c>true</c>, each tracked path is restored to its own original absolute source
    /// location (via <see cref="AbsolutePathMirrorMapper.FromMirrorPath"/>) instead of under
    /// <paramref name="outRoot"/>.
    /// </param>
    /// <exception cref="NoSuchDirectoryException">
    /// No tracked path, at any point in history, falls under <paramref name="directoryPath"/>.
    /// </exception>
    /// <exception cref="RestoreDestinationInMirrorException">
    /// Any computed destination resolves inside the profile's live mirror.
    /// </exception>
    public DirectoryRestorePlan PlanDirectoryRestore(string directoryPath, DateTimeOffset? asOf, string? outRoot, bool inPlace)
    {
        var prefix = HistoryPaths.NormalizeDirectoryPrefix(directoryPath);
        var historical = asOf is null ? repository.GetCurrentState() : repository.GetStateAsOf(asOf.Value);
        var current = repository.GetCurrentState();
        var anyTracked = false;

        string ComputeDestination(string relativePath, string subPath) =>
            inPlace ? AbsolutePathMirrorMapper.FromMirrorPath(relativePath) : Path.Combine(outRoot!, subPath);

        var toWrite = new List<DirectoryRestoreEntry>();
        var skipped = new List<string>();
        var historicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, state) in historical)
        {
            if (!HistoryPaths.TryGetDescendantSubPath(path, prefix, out var subPath))
            {
                continue;
            }

            anyTracked = true;
            historicalPaths.Add(path);

            // A path tracked as live at the requested date as a symlink/junction has no
            // stored content to extract (its ContentHash is always null) - it is
            // reported as skipped rather than written, and (via historicalPaths above)
            // also never added to ToRemove even though it contributes nothing to
            // ToWrite, per the "Restore a directory at a given date" requirement.
            if (state.IsLinked)
            {
                skipped.Add(path);
                continue;
            }

            toWrite.Add(new DirectoryRestoreEntry(path, state.ContentHash!, state.Size, ComputeDestination(path, subPath)));
        }

        var toRemove = new List<string>();
        foreach (var (path, _) in current)
        {
            if (!HistoryPaths.TryGetDescendantSubPath(path, prefix, out var subPath))
            {
                continue;
            }

            anyTracked = true;
            if (historicalPaths.Contains(path))
            {
                continue;
            }

            var destination = ComputeDestination(path, subPath);
            if (contentStore.TargetExists(destination))
            {
                toRemove.Add(destination);
            }
        }

        if (!anyTracked)
        {
            anyTracked = repository.GetTombstones(asOf)
                .Any(record => HistoryPaths.TryGetDescendantSubPath(record.RelativePath, prefix, out _));
        }

        if (!anyTracked)
        {
            throw new NoSuchDirectoryException(directoryPath);
        }

        foreach (var destination in toWrite.Select(e => e.DestinationPath).Concat(toRemove))
        {
            if (contentStore.IsWithinMirror(destination))
            {
                throw new RestoreDestinationInMirrorException(destination);
            }
        }

        return new DirectoryRestorePlan(toWrite, toRemove, toWrite.Sum(e => e.Size), skipped);
    }

    /// <summary>
    /// Executes a previously computed <see cref="DirectoryRestorePlan"/>: reports the plan's
    /// total byte count once via <paramref name="onSizeResolved"/>, then removes every
    /// <see cref="DirectoryRestorePlan.ToRemove"/> destination (fast, with no byte content to
    /// report), and finally writes every <see cref="DirectoryRestorePlan.ToWrite"/> entry,
    /// forwarding <paramref name="onBytesCopied"/> through each entry's
    /// <see cref="IContentStore.ExtractTo"/> call so a caller can render one
    /// continuously-advancing progress indicator across the whole operation.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cooperative "stop starting new work" signal, set by a graceful Ctrl+C stop (see
    /// <c>Vara.Cli.Program</c>'s <c>Console.CancelKeyPress</c> handler). Checked between each
    /// planned path written or removed; whatever was already applied stays applied - see the
    /// snapshot-history spec's "Graceful cancellation of a directory restore via Ctrl+C"
    /// requirement.
    /// </param>
    /// <returns><see langword="true"/> when the plan was only partially applied because a graceful Ctrl+C stop was requested
    /// before every planned path was written/removed; <see langword="false"/> when the whole plan was applied.</returns>
    public bool ExecuteDirectoryRestore(
        DirectoryRestorePlan plan,
        Action<long>? onBytesCopied = null,
        Action<long>? onSizeResolved = null,
        CancellationToken cancellationToken = default)
    {
        onSizeResolved?.Invoke(plan.TotalBytes);

        foreach (var destination in plan.ToRemove)
        {
            // Cooperative cancellation: stop starting any further planned removal/write once a
            // graceful Ctrl+C stop has been requested. Whatever has already been applied stays
            // applied (this loop is sequential, so nothing is "in flight" here beyond the
            // current iteration) - mirroring BackupExecutor's own cancellation checks.
            if (cancellationToken.IsCancellationRequested)
            {
                return true;
            }

            contentStore.RemoveExtractedFile(destination);
        }

        foreach (var entry in plan.ToWrite)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return true;
            }

            contentStore.ExtractTo(entry.ContentHash, entry.DestinationPath, onBytesCopied);
        }

        return false;
    }

    /// <summary>
    /// Enumerates the candidate snapshots the directory restore snapshot picker offers for
    /// <paramref name="directoryPath"/>: every recorded snapshot, in <see cref="SnapshotStatus.Complete"/>
    /// or <see cref="SnapshotStatus.Cancelled"/> status, for which at least one file-version
    /// record exists under the directory - a snapshot in <see cref="SnapshotStatus.Running"/> or
    /// <see cref="SnapshotStatus.Failed"/> status is never included, per the snapshot-history
    /// capability's "Interactive and explicit snapshot selection for a directory restore"
    /// requirement. Returned most-recent-first. Empty (not an exception) when the directory has
    /// no recorded history at all - the caller decides what "no candidates" means (for the
    /// interactive picker, only the "current tracked state" entry is offered).
    /// </summary>
    public IReadOnlyList<DirectorySnapshotCandidate> ListDirectorySnapshotCandidates(string directoryPath)
    {
        var prefix = HistoryPaths.NormalizeDirectoryPrefix(directoryPath);
        var rows = repository.GetFileHistoryUnderPrefix(prefix);
        if (rows.Count == 0)
        {
            return [];
        }

        var snapshotsById = repository.ListSnapshots().ToDictionary(s => s.Id);

        var candidates = new List<DirectorySnapshotCandidate>();
        foreach (var group in rows.GroupBy(r => r.SnapshotId))
        {
            if (!snapshotsById.TryGetValue(group.Key, out var snapshot)
                || snapshot.Status is not (SnapshotStatus.Complete or SnapshotStatus.Cancelled))
            {
                continue;
            }

            var added = 0;
            var changed = 0;
            var moved = 0;
            var deleted = 0;
            var netBytesDelta = 0L;
            foreach (var row in group)
            {
                switch (row.ChangeKind)
                {
                    case FileChangeKind.AddedChange:
                        added++;
                        netBytesDelta += row.Size;
                        break;
                    case FileChangeKind.ChangedChange:
                        changed++;
                        netBytesDelta += row.Size;
                        break;
                    case FileChangeKind.MovedChange:
                        // A row of a move that lands inside the directory (whether the move
                        // stayed entirely within it or brought a path in from outside) adds
                        // that path's bytes to the directory; see GetFileHistoryUnderPrefix's
                        // own documentation for why the corresponding Deleted row (at the old
                        // path) is the only other row this move could have produced.
                        moved++;
                        netBytesDelta += row.Size;
                        break;
                    case FileChangeKind.DeletedChange:
                        deleted++;
                        netBytesDelta -= row.Size;
                        break;
                    case FileChangeKind.LinkedChange:
                        break;
                }
            }

            var (totalFiles, totalBytes, totalLinks) = DirectoryTotalsAsOf(prefix, snapshot.StartedAt);
            candidates.Add(new DirectorySnapshotCandidate(
                snapshot, added, changed, moved, deleted, netBytesDelta, totalFiles, totalBytes, totalLinks));
        }

        return candidates.OrderByDescending(c => c.Snapshot.Id).ToList();
    }

    /// <summary>
    /// Resolves the point in time a <c>--snapshot &lt;id&gt;</c> recursive restore should target:
    /// <paramref name="snapshotId"/> must be a member of
    /// <see cref="ListDirectorySnapshotCandidates"/> for <paramref name="directoryPath"/>, so the
    /// interactive picker and this non-interactive equivalent can never disagree about what
    /// counts as a valid target.
    /// </summary>
    /// <exception cref="NoMatchingDirectorySnapshotException">
    /// <paramref name="snapshotId"/> does not exist, is not in <see cref="SnapshotStatus.Complete"/>
    /// or <see cref="SnapshotStatus.Cancelled"/> status, or has no file-version record under
    /// <paramref name="directoryPath"/>.
    /// </exception>
    public DateTimeOffset ResolveDirectorySnapshot(string directoryPath, long snapshotId)
    {
        var match = ListDirectorySnapshotCandidates(directoryPath).FirstOrDefault(c => c.Snapshot.Id == snapshotId);
        return match?.Snapshot.StartedAt ?? throw new NoMatchingDirectorySnapshotException(directoryPath, snapshotId);
    }

    /// <summary>
    /// The directory's total live-file count, byte size, and symlink/junction count as of
    /// <paramref name="asOf"/>, scoped to <paramref name="prefix"/> - shared by
    /// <see cref="ListDirectorySnapshotCandidates"/> for each candidate's point-in-time totals.
    /// </summary>
    private (int TotalFiles, long TotalBytes, int TotalLinks) DirectoryTotalsAsOf(string prefix, DateTimeOffset asOf)
    {
        var state = repository.GetStateAsOf(asOf);
        var totalFiles = 0;
        var totalBytes = 0L;
        var totalLinks = 0;
        foreach (var (path, fileState) in state)
        {
            if (!HistoryPaths.TryGetDescendantSubPath(path, prefix, out _))
            {
                continue;
            }

            if (fileState.IsLinked)
            {
                totalLinks++;
            }
            else
            {
                totalFiles++;
                totalBytes += fileState.Size;
            }
        }

        return (totalFiles, totalBytes, totalLinks);
    }
}
