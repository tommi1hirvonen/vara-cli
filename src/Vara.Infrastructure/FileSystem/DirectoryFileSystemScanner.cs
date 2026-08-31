using Microsoft.Extensions.FileSystemGlobbing;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;

namespace Vara.Infrastructure.FileSystem;

/// <summary>
/// Walks a profile's configured sources on disk. Recurses manually (rather than via
/// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>) so that
/// symlinks, junctions, and other reparse points can be detected and reported without
/// ever being traversed - the backup-execution spec requires they are recorded, not
/// followed. A file, directory, or link that cannot be read (for example, an
/// ACL-protected path) never throws out of <see cref="Scan"/> - it is recorded as a
/// <see cref="ScanFailure"/> and skipped instead, per the backup-execution spec's
/// "Unreadable files do not abort the run" requirement.
/// </summary>
public sealed class DirectoryFileSystemScanner : IFileSystemScanner
{
    public ScanResult Scan(IReadOnlyList<Source> sources)
    {
        // Failures is a shared mutable list captured by the lazily-evaluated Entries
        // enumerable below: it fills in as Entries is walked, and is only guaranteed
        // complete once Entries has been fully enumerated by the caller.
        var failures = new List<ScanFailure>();
        return new ScanResult(EnumerateEntries(sources, failures), failures);
    }

    private static IEnumerable<ScannedEntry> EnumerateEntries(IReadOnlyList<Source> sources, List<ScanFailure> failures)
    {
        foreach (var source in sources)
        {
            foreach (var entry in ScanSource(source, failures))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<ScannedEntry> ScanSource(Source source, List<ScanFailure> failures)
    {
        if (File.Exists(source.Path))
        {
            // The profile schema also allows a source to point directly at a single file.
            var fileName = Path.GetFileName(source.Path);
            var entry = ToEntry(source.Path, fileName, failures);
            if (entry is not null)
            {
                yield return entry;
            }

            yield break;
        }

        if (!Directory.Exists(source.Path))
        {
            yield break;
        }

        var matcher = BuildGlobMatcher(source);
        var root = source.Path.TrimEnd('\\', '/');

        foreach (var path in Walk(root, source.Recursive, root, failures))
        {
            var relativePath = Path.GetRelativePath(root, path);

            if (IsExcluded(relativePath, source) || (matcher is not null && !MatchesGlob(matcher, relativePath)))
            {
                continue;
            }

            var entry = ToEntry(path, relativePath, failures);
            if (entry is not null)
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Walks a directory tree, yielding the path of every reportable entry: regular
    /// files, and reparse points (symlinks/junctions) which are reported once but never
    /// descended into. Plain (non-reparse) directories are recursed through but not
    /// themselves yielded.
    /// </summary>
    private static IEnumerable<string> Walk(string directory, bool recursive, string root, List<ScanFailure> failures)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            failures.Add(new ScanFailure(Path.GetRelativePath(root, directory), ScanFailureReason.UnreadableDirectory));
            yield break;
        }

        foreach (var entryPath in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new ScanFailure(Path.GetRelativePath(root, entryPath), ScanFailureReason.UnreadableEntry));
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
                    foreach (var nested in Walk(entryPath, recursive, root, failures))
                    {
                        yield return nested;
                    }
                }

                continue;
            }

            yield return entryPath;
        }
    }

    private static ScannedEntry? ToEntry(string absolutePath, string relativePath, List<ScanFailure> failures)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(absolutePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new ScanFailure(relativePath, ScanFailureReason.UnreadableEntry));
            return null;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return new ScannedEntry(relativePath, absolutePath, 0, DateTimeOffset.MinValue, IsLink: true, TryGetLinkTarget(absolutePath));
        }

        try
        {
            var info = new FileInfo(absolutePath);
            return new ScannedEntry(relativePath, absolutePath, info.Length, info.LastWriteTimeUtc, IsLink: false, LinkTarget: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new ScanFailure(relativePath, ScanFailureReason.UnreadableEntry));
            return null;
        }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
