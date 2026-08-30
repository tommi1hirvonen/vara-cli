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
public sealed record FileVersionRecord(
    long Id,
    long SnapshotId,
    string RelativePath,
    string? PreviousRelativePath,
    string ContentHash,
    long Size,
    DateTimeOffset SourceModifiedAt,
    FileChangeKind ChangeKind,
    DateTimeOffset RecordedAt);

/// <summary>
/// The current (as of the latest snapshot) state of a tracked, non-deleted file path.
/// </summary>
public sealed record CurrentFileState(
    string RelativePath,
    string ContentHash,
    long Size,
    DateTimeOffset SourceModifiedAt);
