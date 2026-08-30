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

/// <summary>
/// Walks a profile's configured sources, honoring recursion, exclude, and glob rules,
/// and reports symlinks/junctions/reparse points without following them.
/// </summary>
public interface IFileSystemScanner
{
    IEnumerable<ScannedEntry> Scan(IReadOnlyList<Source> sources);
}
