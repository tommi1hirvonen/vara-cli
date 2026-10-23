using Vara.Core.Abstractions;
using Vara.Core.Snapshots;

namespace Vara.Application.History;

/// <summary>
/// Read-only browsing of the recorded snapshots and versions of a single profile, per the
/// snapshot-history and backup-browsing specs. Reading or restoring file content lives in
/// <see cref="FileVersionService"/> and <see cref="DirectoryRestoreService"/>; this service
/// needs only the profile's manifest.
/// </summary>
public sealed class SnapshotHistoryService(ISnapshotRepository repository)
{
    /// <summary>All recorded snapshots for the profile, most recent first.</summary>
    public IReadOnlyList<Snapshot> ListSnapshots() => repository.ListSnapshots();

    /// <summary>
    /// Builds a sparse directory rollup for the recorded changes in one snapshot, optionally
    /// retaining the changed file rows for presentation. Returns <see langword="null"/> when
    /// the snapshot id is not recorded, and a report with no directories when the snapshot has
    /// no events under the requested scope.
    /// </summary>
    public SnapshotChangeReport? GetSnapshotChanges(long snapshotId, string directoryPath, bool includeFiles = false)
    {
        var snapshot = repository.ListSnapshots().FirstOrDefault(s => s.Id == snapshotId);
        if (snapshot is null)
        {
            return null;
        }

        var prefix = HistoryPaths.NormalizeDirectoryPrefix(directoryPath);
        var rows = repository.GetSnapshotFileChanges(snapshotId, prefix);
        return new SnapshotChangeReport(
            snapshot,
            directoryPath,
            rows.Count == 0 ? [] : SnapshotChangeReportBuilder.Build(prefix, rows, includeFiles));
    }

    /// <summary>
    /// The full recorded history of a path, most recent first.
    /// </summary>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    public IReadOnlyList<FileVersionRecord> GetFileHistory(string relativePath)
    {
        var history = repository.GetFileHistory(relativePath);
        if (history.Count == 0)
        {
            throw new NoHistoryForPathException(relativePath);
        }

        return history;
    }

    /// <summary>
    /// Lists the immediate (one-level) contents of <paramref name="directoryPath"/> within the
    /// profile's mirror, per the backup-browsing capability's directory-listing requirements.
    /// Reflects the current state when <paramref name="asOf"/> is <c>null</c>, or the state as
    /// of that date otherwise. When <paramref name="includeDeleted"/> is <c>true</c>, deleted
    /// entries (and wholly-deleted subdirectories) are interleaved among the live ones, each
    /// marked <see cref="DirectoryEntryStatus.Deleted"/> or, if the entry was moved elsewhere
    /// rather than deleted outright, <see cref="DirectoryEntryStatus.Moved"/>.
    /// </summary>
    /// <exception cref="NoSuchDirectoryException">
    /// No tracked path, at any point in history, falls under <paramref name="directoryPath"/> -
    /// regardless of <paramref name="includeDeleted"/>, since a directory that currently has only
    /// deleted content is still a directory that was tracked.
    /// </exception>
    public IReadOnlyList<DirectoryEntry> ListDirectory(string directoryPath, DateTimeOffset? asOf, bool includeDeleted)
    {
        var prefix = HistoryPaths.NormalizeDirectoryPrefix(directoryPath);
        var live = asOf is null ? repository.GetCurrentState() : repository.GetStateAsOf(asOf.Value);
        var tombstones = repository.GetTombstones(asOf);
        var moveOrigins = repository.GetMoveOrigins();

        var entries = new Dictionary<string, DirectoryEntry>(StringComparer.OrdinalIgnoreCase);
        var anyTracked = false;

        foreach (var (path, state) in live)
        {
            if (!HistoryPaths.TryGetImmediateChild(path, prefix, out var name, out var isLeaf))
            {
                continue;
            }

            anyTracked = true;
            if (isLeaf)
            {
                entries[name] = new DirectoryEntry(name, DirectoryEntryKind.File, DirectoryEntryStatus.Live, state.Size);
            }
            else if (!entries.TryGetValue(name, out var existingDir) || existingDir.Status != DirectoryEntryStatus.Live)
            {
                entries[name] = new DirectoryEntry(name, DirectoryEntryKind.Directory, DirectoryEntryStatus.Live, null);
            }
        }

        foreach (var record in tombstones)
        {
            if (!HistoryPaths.TryGetImmediateChild(record.RelativePath, prefix, out var name, out var isLeaf))
            {
                continue;
            }

            anyTracked = true;
            if (!includeDeleted)
            {
                continue;
            }

            if (isLeaf)
            {
                if (entries.ContainsKey(name))
                {
                    continue;
                }

                var moved = moveOrigins.TryGetValue(record.RelativePath, out var movedTo);
                entries[name] = new DirectoryEntry(
                    name,
                    DirectoryEntryKind.File,
                    moved ? DirectoryEntryStatus.Moved : DirectoryEntryStatus.Deleted,
                    record.Size,
                    moved ? movedTo : null);
            }
            else if (!entries.ContainsKey(name))
            {
                entries[name] = new DirectoryEntry(name, DirectoryEntryKind.Directory, DirectoryEntryStatus.Deleted, null);
            }
        }

        if (!anyTracked)
        {
            throw new NoSuchDirectoryException(directoryPath);
        }

        return entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Reports deleted files, most recently deleted first, optionally scoped to a subtree
    /// (<paramref name="directoryPath"/>) and/or bounded to deletions recorded at or after
    /// <paramref name="since"/>, per the backup-browsing capability's recently-deleted report.
    /// </summary>
    public IReadOnlyList<FileVersionRecord> ListDeleted(string? directoryPath, DateTimeOffset? since)
    {
        IEnumerable<FileVersionRecord> tombstones = repository.GetTombstones(asOf: null);

        var prefix = HistoryPaths.NormalizeDirectoryPrefix(directoryPath ?? string.Empty);
        if (prefix.Length > 0)
        {
            tombstones = tombstones.Where(r => r.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        if (since is not null)
        {
            tombstones = tombstones.Where(r => r.RecordedAt >= since.Value);
        }

        return tombstones.OrderByDescending(r => r.RecordedAt).ToList();
    }
}
