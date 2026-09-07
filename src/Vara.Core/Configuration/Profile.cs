namespace Vara.Core.Configuration;

/// <summary>
/// A named backup profile: a target mirror root and one or more source locations,
/// with an optional tiered retention policy. Construction enforces the structural
/// invariants required by the profile-config capability (non-empty name/target,
/// at least one source).
/// </summary>
public sealed record Profile
{
    /// <param name="validateSourceOverlap">
    /// When <see langword="true"/> (the default), rejects a <paramref name="targetRoot"/> that is
    /// equal to, an ancestor of, or a descendant of any of <paramref name="sources"/>' paths, and
    /// rejects any of <paramref name="sources"/>' paths that overlap each other in the same way. Pass
    /// <see langword="false"/> only for the synthetic placeholder profile built by
    /// <c>ProfileResolver.TryResolveFromWorkingDirectory</c>, whose single placeholder
    /// <see cref="Source"/> is intentionally the same directory as the target root.
    /// </param>
    public Profile(
        string name,
        string targetRoot,
        IReadOnlyList<Source> sources,
        RetentionPolicy? retention,
        ConcurrencySettings? concurrency = null,
        bool validateSourceOverlap = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        if (!System.IO.Path.IsPathFullyQualified(targetRoot))
        {
            throw new ArgumentException(
                $"Target root '{targetRoot}' must be a fully-qualified, absolute path.", nameof(targetRoot));
        }

        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("A profile must define at least one source.", nameof(sources));
        }

        if (validateSourceOverlap)
        {
            foreach (var source in sources)
            {
                if (PathsOverlap(targetRoot, source.Path))
                {
                    throw new ArgumentException(
                        $"Target root '{targetRoot}' overlaps source path '{source.Path}'. A profile's target must not be the same as, contain, or be contained by any of its own sources.",
                        nameof(targetRoot));
                }
            }

            for (var i = 0; i < sources.Count; i++)
            {
                for (var j = i + 1; j < sources.Count; j++)
                {
                    if (PathsOverlap(sources[i].Path, sources[j].Path))
                    {
                        throw new ArgumentException(
                            $"Source path '{sources[i].Path}' overlaps source path '{sources[j].Path}'. A profile's sources must not be the same as, contain, or be contained by any of its own other sources.",
                            nameof(sources));
                    }
                }
            }
        }

        Name = name;
        TargetRoot = targetRoot;
        Sources = sources;
        Retention = retention;
        Concurrency = concurrency;
    }

    public string Name { get; }
    public string TargetRoot { get; }
    public IReadOnlyList<Source> Sources { get; }
    public RetentionPolicy? Retention { get; }
    public ConcurrencySettings? Concurrency { get; }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="targetRoot"/> and <paramref name="sourcePath"/>
    /// are the same location, or one is an ancestor directory of the other. Comparison is
    /// case-insensitive and ignores a trailing directory separator, mirroring the normalization
    /// the file system scanner applies when matching exclude paths.
    /// </summary>
    private static bool PathsOverlap(string targetRoot, string sourcePath)
    {
        var normalizedTarget = NormalizePath(targetRoot);
        var normalizedSource = NormalizePath(sourcePath);

        return string.Equals(normalizedTarget, normalizedSource, StringComparison.OrdinalIgnoreCase)
            || normalizedSource.StartsWith(normalizedTarget + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.StartsWith(normalizedSource + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) =>
        path.Trim().TrimEnd('\\', '/').Replace('/', System.IO.Path.DirectorySeparatorChar);
}

/// <summary>
/// A single source location contributing files to a profile's mirror.
/// </summary>
public sealed record Source
{
    public Source(
        string path,
        bool recursive = true,
        IReadOnlyList<string>? excludes = null,
        IReadOnlyList<string>? includeGlobs = null,
        IReadOnlyList<string>? excludeGlobs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                $"Source path '{path}' must be a fully-qualified, absolute path.", nameof(path));
        }

        Path = path;
        Recursive = recursive;
        Excludes = excludes ?? [];
        IncludeGlobs = includeGlobs ?? [];
        ExcludeGlobs = excludeGlobs ?? [];
    }

    public string Path { get; }
    public bool Recursive { get; }
    public IReadOnlyList<string> Excludes { get; }
    public IReadOnlyList<string> IncludeGlobs { get; }
    public IReadOnlyList<string> ExcludeGlobs { get; }
}
