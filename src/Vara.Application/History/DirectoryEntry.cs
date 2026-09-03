namespace Vara.Application.History;

/// <summary>
/// The kind of a <see cref="DirectoryEntry"/>: a tracked file, or a directory synthesized from
/// the tracked paths beneath it (the manifest only tracks files, never directories directly).
/// </summary>
public enum DirectoryEntryKind
{
    File,
    Directory,
}

/// <summary>
/// The status of a <see cref="DirectoryEntry"/> within a directory listing: currently live, or
/// (only present when a listing includes deleted entries) deleted outright or moved elsewhere.
/// A synthesized directory is <see cref="Live"/> if any of its tracked descendants are still
/// live, or <see cref="Deleted"/> if every one of them has been deleted.
/// </summary>
public enum DirectoryEntryStatus
{
    Live,
    Deleted,
    Moved,
}

/// <summary>
/// One immediate entry in a directory listing produced by
/// <see cref="SnapshotHistoryService.ListDirectory"/>, per the backup-browsing capability.
/// </summary>
/// <param name="Name">The entry's own name (the final path segment), not its full path.</param>
/// <param name="Kind">Whether this is a tracked file or a synthesized subdirectory.</param>
/// <param name="Status">Live, deleted, or moved elsewhere.</param>
/// <param name="Size">The file's size, when known; <c>null</c> for directories.</param>
/// <param name="MovedTo">
/// When <see cref="Status"/> is <see cref="DirectoryEntryStatus.Moved"/>, the mirror-relative
/// path the entry now lives at; otherwise <c>null</c>.
/// </param>
public sealed record DirectoryEntry(
    string Name,
    DirectoryEntryKind Kind,
    DirectoryEntryStatus Status,
    long? Size,
    string? MovedTo = null);
