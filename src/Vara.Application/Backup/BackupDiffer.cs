using Vara.Core.Abstractions;
using Vara.Core.Snapshots;

namespace Vara.Application.Backup;

/// <summary>
/// Compares scanned source entries against the manifest's current-state view to
/// determine what changed since the last snapshot - a cheap size/modified-time
/// comparison only, per the backup-execution spec's "Incremental change detection"
/// requirement (no content is read at this stage). A manifest-tracked path that a
/// scan failure prevented from being verified this run (a missing source, an
/// unreadable subtree, or an unreadable file) is never classified as deleted - see
/// the backup-execution spec's "Unreadable files do not abort the run" requirement.
/// </summary>
public sealed class BackupDiffer
{
    public DiffResult Diff(
        IEnumerable<ScannedEntry> scanned,
        IReadOnlyDictionary<string, CurrentFileState> currentState,
        IReadOnlyList<ScanFailure> failures)
    {
        var pending = new List<PendingChange>();
        var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in scanned)
        {
            if (entry.IsLink)
            {
                // Symlinks/junctions are recorded by the scanner but never diffed as
                // content - the backup-execution spec requires they are never followed.
                continue;
            }

            scannedPaths.Add(entry.RelativePath);

            if (currentState.TryGetValue(entry.RelativePath, out var current))
            {
                if (current.Size == entry.Size && current.SourceModifiedAt == entry.ModifiedAt)
                {
                    continue; // unchanged - no manifest row needed
                }

                pending.Add(new PendingChange(entry, PendingChangeKind.Changed));
            }
            else
            {
                pending.Add(new PendingChange(entry, PendingChangeKind.Added));
            }
        }

        // A path that wasn't re-observed this run is only a genuine deletion if nothing
        // prevented us from verifying it: a scan failure (a missing source, an
        // unreadable subtree, or an unreadable file) means we simply don't know whether
        // the path still exists, so it must be left alone rather than misclassified as
        // deleted - per the backup-execution spec's "Unreadable files do not abort the
        // run" requirement.
        var deleted = currentState.Keys
            .Where(path => !scannedPaths.Contains(path))
            .Where(path => !IsUnderAnyFailure(path, failures))
            .ToList();

        return new DiffResult(pending, deleted);
    }

    /// <summary>
    /// True when <paramref name="path"/> (a mirror-space, manifest-tracked path) equals, or is
    /// nested under, any failure's own mirror-space path - a segment-boundary-aware,
    /// case-insensitive comparison mirroring <c>DirectoryFileSystemScanner.IsExcluded</c>'s
    /// exclude-list matching, so a failure whose mirror path is <c>C\Users\john</c> suppresses
    /// <c>C\Users\john\file.txt</c> but not an unrelated sibling like <c>C\Users\john2\file.txt</c>.
    /// </summary>
    private static bool IsUnderAnyFailure(string path, IReadOnlyList<ScanFailure> failures)
    {
        var normalizedPath = path.Replace('\\', '/');

        foreach (var failure in failures)
        {
            var normalizedFailurePath = failure.MirrorPath.Replace('\\', '/');
            if (string.Equals(normalizedPath, normalizedFailurePath, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(normalizedFailurePath + '/', StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
