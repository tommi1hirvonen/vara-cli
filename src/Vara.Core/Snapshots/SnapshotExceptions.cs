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
/// A directory-listing command (backup-browsing capability) was invoked for a directory
/// path under which no tracked path, at any point in history, falls - regardless of
/// whether deleted entries were requested, since a directory with only deleted content is
/// still a directory that was tracked.
/// </summary>
public sealed class NoSuchDirectoryException(string directoryPath)
    : Exception($"No such directory is recorded in the backup: '{directoryPath}'.")
{
    public string DirectoryPath { get; } = directoryPath;
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

/// <summary>
/// A restore was requested to a destination path that resolves inside the profile's live
/// mirror. Refused unconditionally, since restore's contract is to extract content without
/// touching the live mirror.
/// </summary>
public sealed class RestoreDestinationInMirrorException(string destinationPath)
    : Exception($"Restore destination '{destinationPath}' is inside the profile's live mirror and would corrupt it. Choose a destination outside the mirror.")
{
    public string DestinationPath { get; } = destinationPath;
}

/// <summary>
/// A restore was requested to a destination path that already contains a file, and no
/// explicit overwrite override was given.
/// </summary>
public sealed class DestinationExistsException(string destinationPath)
    : Exception($"Restore destination '{destinationPath}' already exists. Pass --force to overwrite it.")
{
    public string DestinationPath { get; } = destinationPath;
}

/// <summary>
/// A recursive directory restore (<c>restore --recursive</c>) was planned for a profile
/// while running with a non-interactive input stream and without <c>--force</c> - so the
/// operation's single required confirmation, covering every planned write and removal,
/// cannot be shown interactively and was not explicitly authorized. See the
/// snapshot-history spec's "Single confirmation for a directory restore" requirement.
/// </summary>
public sealed class RestoreDirectoryConfirmationRequiredException(string directoryPath, int writeCount, int removeCount)
    : Exception(
        $"Restoring directory '{directoryPath}' will write {writeCount} file(s)" +
        (removeCount > 0 ? $" and remove {removeCount} file(s)" : string.Empty) +
        ". Pass --force to confirm, or re-run interactively to be prompted.")
{
    public string DirectoryPath { get; } = directoryPath;
    public int WriteCount { get; } = writeCount;
    public int RemoveCount { get; } = removeCount;
}
