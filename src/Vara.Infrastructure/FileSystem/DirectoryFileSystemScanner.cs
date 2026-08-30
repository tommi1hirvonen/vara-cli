using Microsoft.Extensions.FileSystemGlobbing;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;

namespace Vara.Infrastructure.FileSystem;

/// <summary>
/// Walks a profile's configured sources on disk. Recurses manually (rather than via
/// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>) so that
/// symlinks, junctions, and other reparse points can be detected and reported without
/// ever being traversed - the backup-execution spec requires they are recorded, not
/// followed.
/// </summary>
public sealed class DirectoryFileSystemScanner : IFileSystemScanner
{
    public IEnumerable<ScannedEntry> Scan(IReadOnlyList<Source> sources)
    {
        foreach (var source in sources)
        {
            foreach (var entry in ScanSource(source))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<ScannedEntry> ScanSource(Source source)
    {
        if (File.Exists(source.Path))
        {
            // The profile schema also allows a source to point directly at a single file.
            var fileName = Path.GetFileName(source.Path);
            yield return ToEntry(source.Path, fileName);
            yield break;
        }

        if (!Directory.Exists(source.Path))
        {
            yield break;
        }

        var matcher = BuildGlobMatcher(source);
        var root = source.Path.TrimEnd('\\', '/');

        foreach (var path in Walk(root, source.Recursive))
        {
            var relativePath = Path.GetRelativePath(root, path);

            if (IsExcluded(relativePath, source) || (matcher is not null && !MatchesGlob(matcher, relativePath)))
            {
                continue;
            }

            yield return ToEntry(path, relativePath);
        }
    }

    /// <summary>
    /// Walks a directory tree, yielding the path of every reportable entry: regular
    /// files, and reparse points (symlinks/junctions) which are reported once but never
    /// descended into. Plain (non-reparse) directories are recursed through but not
    /// themselves yielded.
    /// </summary>
    private static IEnumerable<string> Walk(string directory, bool recursive)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var entryPath in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entryPath);
            }
            catch (IOException)
            {
                continue;
            }

            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);

            if (isReparsePoint)
            {
                // Report the link itself, but never traverse into it - regardless of
                // whether it links to a file or a directory.
                yield return entryPath;
                continue;
            }

            if (isDirectory)
            {
                if (recursive)
                {
                    foreach (var nested in Walk(entryPath, recursive))
                    {
                        yield return nested;
                    }
                }

                continue;
            }

            yield return entryPath;
        }
    }

    private static ScannedEntry ToEntry(string absolutePath, string relativePath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(absolutePath);
        }
        catch (IOException)
        {
            attributes = FileAttributes.Normal;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return new ScannedEntry(relativePath, absolutePath, 0, DateTimeOffset.MinValue, IsLink: true, TryGetLinkTarget(absolutePath));
        }

        var info = new FileInfo(absolutePath);
        return new ScannedEntry(relativePath, absolutePath, info.Length, info.LastWriteTimeUtc, IsLink: false, LinkTarget: null);
    }

    private static bool IsExcluded(string relativePath, Source source)
    {
        foreach (var exclude in source.Excludes)
        {
            var normalized = exclude.Trim().TrimEnd('\\', '/');
            if (string.Equals(relativePath, normalized, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Matcher? BuildGlobMatcher(Source source)
    {
        if (source.IncludeGlobs.Count == 0 && source.ExcludeGlobs.Count == 0)
        {
            return null;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(source.IncludeGlobs.Count > 0 ? source.IncludeGlobs : ["**/*"]);
        if (source.ExcludeGlobs.Count > 0)
        {
            matcher.AddExcludePatterns(source.ExcludeGlobs);
        }

        return matcher;
    }

    private static bool MatchesGlob(Matcher matcher, string relativePath) =>
        matcher.Match(relativePath.Replace('\\', '/')).HasMatches;

    private static string? TryGetLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
