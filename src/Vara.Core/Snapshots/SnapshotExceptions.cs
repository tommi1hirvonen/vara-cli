namespace Vara.Core.Snapshots;

/// <summary>
/// A history/restore command was invoked for a path never present in any recorded
/// snapshot (snapshot-history spec: "Unknown path or version reported clearly").
/// </summary>
public sealed class NoHistoryForPathException(string relativePath)
    : Exception($"No history exists for path '{relativePath}'.")
{
    public string RelativePath { get; } = relativePath;
}

/// <summary>
/// A restore was requested for a date or version that does not correspond to any
/// recorded content for the given path.
/// </summary>
public sealed class NoMatchingVersionException : Exception
{
    public string RelativePath { get; }
    public DateTimeOffset? AsOf { get; }
    public long? VersionId { get; }

    public NoMatchingVersionException(string relativePath, DateTimeOffset asOf)
        : base($"No version of '{relativePath}' existed as of {asOf:O}.")
    {
        RelativePath = relativePath;
        AsOf = asOf;
    }

    public NoMatchingVersionException(string relativePath, long versionId)
        : base($"No version with id {versionId} was found for path '{relativePath}'.")
    {
        RelativePath = relativePath;
        VersionId = versionId;
    }
}
