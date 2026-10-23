using Vara.Core.Snapshots;

namespace Vara.Application.History;

/// <summary>
/// One file to be written as part of a directory restore's planned reconstruction of a point in
/// time, per <see cref="SnapshotHistoryService.PlanDirectoryRestore"/>.
/// </summary>
public sealed record DirectoryRestoreEntry(string RelativePath, string ContentHash, long Size, string DestinationPath);

/// <summary>
/// The full set of writes and removals a directory restore will perform, computed entirely from
/// manifest data before any file is touched, per the snapshot-history capability's "Restore a
/// directory at a given date" requirement - so a single confirmation can report exact counts
/// before anything changes. <see cref="Skipped"/> lists every path tracked as live at the
/// requested date as a symlink/junction entry - it has no stored content to extract, so it is
/// omitted from <see cref="ToWrite"/> (and never added to <see cref="ToRemove"/> either) rather
/// than causing the whole restore to fail.
/// </summary>
public sealed record DirectoryRestorePlan(
    IReadOnlyList<DirectoryRestoreEntry> ToWrite,
    IReadOnlyList<string> ToRemove,
    long TotalBytes,
    IReadOnlyList<string> Skipped);

/// <summary>
/// One candidate snapshot offered by the directory restore snapshot picker: a snapshot that
/// recorded at least one file-version change under the requested directory, together with that
/// run's directory-scoped delta statistics and the directory's point-in-time totals as of that
/// snapshot, per the snapshot-history capability's "Interactive and explicit snapshot selection
/// for a directory restore" requirement. <see cref="FilesAdded"/>/<see cref="FilesChanged"/>/
/// <see cref="FilesMoved"/>/<see cref="FilesDeleted"/> and <see cref="NetBytesDelta"/> are scoped
/// to file-version rows recorded under the directory by this specific run; <see cref="TotalFiles"/>/
/// <see cref="TotalBytes"/>/<see cref="TotalLinks"/> are the directory's contents as of this
/// snapshot's <see cref="Snapshots.Snapshot.StartedAt"/>, with symlink/junction entries counted
/// separately from regular files (<see cref="TotalLinks"/>) rather than folded into
/// <see cref="TotalFiles"/>.
/// </summary>
public sealed record DirectorySnapshotCandidate(
    Snapshot Snapshot,
    int FilesAdded,
    int FilesChanged,
    int FilesMoved,
    int FilesDeleted,
    long NetBytesDelta,
    int TotalFiles,
    long TotalBytes,
    int TotalLinks);

/// <summary>
/// One directory in a snapshot's sparse change report. Counts include all change events in
/// this directory and its descendants; <see cref="Files"/> contains only events directly
/// within this directory, and is empty unless file details were requested.
/// </summary>
public sealed record SnapshotChangeDirectory(
    string RelativePath,
    int FilesAdded,
    int FilesChanged,
    int FilesMoved,
    int FilesDeleted,
    int FilesLinked,
    IReadOnlyList<FileVersionRecord> Files);

/// <summary>The recorded events and directory rollups for one snapshot scope.</summary>
public sealed record SnapshotChangeReport(
    Snapshot Snapshot,
    string DirectoryPath,
    IReadOnlyList<SnapshotChangeDirectory> Directories);
