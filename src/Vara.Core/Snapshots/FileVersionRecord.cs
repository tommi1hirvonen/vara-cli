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
/// </summary>
public sealed record FileVersionRecord(
    long Id,
    long SnapshotId,
    string RelativePath,
    string? PreviousRelativePath,
    string ContentHash,
    long Size,
    DateTimeOffset SourceModifiedAt,
    FileChangeKind ChangeKind,
    DateTimeOffset RecordedAt,
    string? QuickHash = null,
    int? QuickHashScheme = null);

/// <summary>
/// The current (as of the latest snapshot) state of a tracked, non-deleted file path.
/// See <see cref="FileVersionRecord.QuickHash"/> for <see cref="QuickHash"/>/
/// <see cref="QuickHashScheme"/>.
/// </summary>
public sealed record CurrentFileState(
    string RelativePath,
    string ContentHash,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? QuickHash = null,
    int? QuickHashScheme = null);
