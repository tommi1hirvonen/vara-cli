using Vara.Core.Configuration;

namespace Vara.Core.Abstractions;

/// <summary>
/// A single file-system entry discovered while scanning a profile's sources.
/// A <see cref="IsLink"/> entry is recorded but its target is never traversed or read.
/// </summary>
public sealed record ScannedEntry(
    string RelativePath,
    string AbsolutePath,
    long Size,
    DateTimeOffset ModifiedAt,
    bool IsLink,
    string? LinkTarget);

/// <summary>Why a path could not be scanned.</summary>
public enum ScanFailureReason
{
    /// <summary>A file (or other non-directory entry) could not be read.</summary>
    UnreadableEntry,

    /// <summary>A directory's contents could not be enumerated; the whole subtree rooted there was skipped.</summary>
    UnreadableDirectory,

    /// <summary>A configured source's path was neither an existing file nor an existing directory at scan
    /// time (for example, an unplugged drive, a drive-letter change, or a typo'd path).</summary>
    SourceUnavailable,
}

/// <summary>
/// A path that could not be scanned (for example, due to a permission error), recorded
/// so its absence from <see cref="ScannedEntry"/> results is not mistaken for the path
/// simply not existing (backup-execution spec: "Unreadable files do not abort the run").
/// </summary>
/// <param name="RelativePath">
/// Source-relative (not mirror-relative) path of the failed file, directory, or source root,
/// for human-facing reporting (for example, in a run's failed-paths summary) - reads naturally,
/// e.g. <c>subdir\locked.txt</c>, rather than a mirror-mangled absolute-looking path.
/// </param>
/// <param name="Reason">Why the path could not be scanned.</param>
/// <param name="MirrorPath">
/// The same failed location expressed in mirror-path space (see <see cref="ScannedEntry.RelativePath"/>
/// and <see cref="Vara.Core.FileSystem.AbsolutePathMirrorMapper"/>), i.e. what a successfully-scanned
/// entry at that same location would have had as its own <see cref="ScannedEntry.RelativePath"/>. Used
/// to match this failure against manifest-tracked paths (which are recorded in mirror-path space)
/// regardless of which source produced them, so a failed scan can suppress deletion classification for
/// every previously recorded path it made it impossible to verify.
/// </param>
public sealed record ScanFailure(string RelativePath, ScanFailureReason Reason, string MirrorPath);

/// <summary>
/// The result of scanning a profile's sources: successfully discovered entries plus
/// any paths that could not be read. <see cref="Entries"/> is lazily evaluated;
/// <see cref="Failures"/> is only guaranteed fully populated once <see cref="Entries"/>
/// has been completely enumerated.
/// </summary>
public sealed record ScanResult(IEnumerable<ScannedEntry> Entries, IReadOnlyList<ScanFailure> Failures);

/// <summary>
/// Walks a profile's configured sources, honoring recursion, exclude, and glob rules,
/// and reports symlinks/junctions/reparse points without following them.
/// </summary>
public interface IFileSystemScanner
{
    ScanResult Scan(IReadOnlyList<Source> sources);
}
