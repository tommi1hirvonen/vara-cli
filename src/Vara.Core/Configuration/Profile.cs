namespace Vara.Core.Configuration;

/// <summary>
/// A named backup profile: a target mirror root and one or more source locations,
/// with an optional tiered retention policy. Construction enforces the structural
/// invariants required by the profile-config capability (non-empty name/target,
/// at least one source).
/// </summary>
public sealed record Profile
{
    public Profile(string name, string targetRoot, IReadOnlyList<Source> sources, RetentionPolicy? retention, ConcurrencySettings? concurrency = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("A profile must define at least one source.", nameof(sources));
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
