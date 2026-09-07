namespace Vara.Core.Snapshots;

/// <summary>
/// The kind of change a persisted file-version record represents. Unchanged files
/// never get a new record - the most recent record for a path is simply carried
/// forward across snapshots that didn't touch it.
/// </summary>
public enum FileChangeKind
{
    Added,
    Changed,
    Moved,
    Deleted,

    /// <summary>
    /// A symbolic link or junction was observed at this path - either for the first
    /// time, or replacing a previously tracked regular file. Distinct from
    /// <see cref="Deleted"/>: the path is still live, just not a regular file with
    /// backed-up content. <see cref="FileVersionRecord.LinkTarget"/> holds the link's
    /// target path; <see cref="FileVersionRecord.ContentHash"/> is <c>null</c> for a
    /// <see cref="Linked"/> row, since the target is never followed or copied into the
    /// content store (backup-execution spec's "Symlinks and junctions are not followed"
    /// requirement).
    /// </summary>
    Linked,
}

/// <summary>
/// One historical entry for a file path: the content it had, when it was recorded,
/// and what kind of change produced this entry. <see cref="PreviousRelativePath"/> is
/// only set for <see cref="FileChangeKind.Moved"/> entries.
/// </summary>
/// <summary>
/// <see cref="QuickHash"/>/<see cref="QuickHashScheme"/> carry a bounded-prefix content
/// signature (see <see cref="Vara.Core.Hashing.QuickHashPolicy"/>) alongside the full
/// <see cref="ContentHash"/>, used to pre-filter move-detection candidates without a
/// full-content read. Both are <c>null</c> for rows recorded before this capability
/// existed or under an incompatible scheme - move detection treats that identically to
/// "no signature available" and falls back to a full-content comparison.
/// <see cref="ContentHash"/> is <c>null</c> for a <see cref="FileChangeKind.Linked"/>
/// row, which carries its target path in <see cref="LinkTarget"/> instead - the two are
/// mutually exclusive, never both set.
/// </summary>
public sealed record FileVersionRecord(
    long Id,
    long SnapshotId,
    string RelativePath,
    string? PreviousRelativePath,
    string? ContentHash,
    long Size,
    DateTimeOffset SourceModifiedAt,
    FileChangeKind ChangeKind,
    DateTimeOffset RecordedAt,
    string? QuickHash = null,
    int? QuickHashScheme = null,
    string? LinkTarget = null);

/// <summary>
/// The current (as of the latest snapshot) state of a tracked, non-deleted file path.
/// See <see cref="FileVersionRecord.QuickHash"/> for <see cref="QuickHash"/>/
/// <see cref="QuickHashScheme"/>. <see cref="IsLinked"/> is <c>true</c> when the most
/// recent record for this path is a <see cref="FileChangeKind.Linked"/> entry, so
/// <see cref="BackupDiffer"/> (Vara.Application.Backup) can tell a symlink that's
/// already tracked as such (no new pending change needed) apart from a path newly
/// becoming a link (which needs one). <see cref="ContentHash"/> is <c>null</c> and
/// <see cref="LinkTarget"/> is set exactly when <see cref="IsLinked"/> is <c>true</c>.
/// </summary>
public sealed record CurrentFileState(
    string RelativePath,
    string? ContentHash,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? QuickHash = null,
    int? QuickHashScheme = null,
    bool IsLinked = false,
    string? LinkTarget = null);
