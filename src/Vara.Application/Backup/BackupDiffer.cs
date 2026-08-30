using Vara.Core.Abstractions;
using Vara.Core.Snapshots;

namespace Vara.Application.Backup;

/// <summary>
/// Compares scanned source entries against the manifest's current-state view to
/// determine what changed since the last snapshot - a cheap size/modified-time
/// comparison only, per the backup-execution spec's "Incremental change detection"
/// requirement (no content is read at this stage).
/// </summary>
public sealed class BackupDiffer
{
    public DiffResult Diff(IEnumerable<ScannedEntry> scanned, IReadOnlyDictionary<string, CurrentFileState> currentState)
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

        var deleted = currentState.Keys.Where(path => !scannedPaths.Contains(path)).ToList();

        return new DiffResult(pending, deleted);
    }
}
