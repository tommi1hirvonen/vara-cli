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
/// A diff was requested where at least one resolved version's recorded size exceeds the diff
/// size limit (snapshot-history spec: "Refusing an oversized version"). Raised before either
/// version's full content is read into memory. Only the side(s) that actually exceed the limit
/// are included in <see cref="LeftSize"/>/<see cref="RightSize"/> and named in the message.
/// </summary>
public sealed class DiffContentTooLargeException : Exception
{
    public string RelativePath { get; }
    public long Limit { get; }
    public long? LeftSize { get; }
    public long? RightSize { get; }

    public DiffContentTooLargeException(string relativePath, long limit, long? leftSize, long? rightSize)
        : base(BuildMessage(relativePath, limit, leftSize, rightSize))
    {
        RelativePath = relativePath;
        Limit = limit;
        LeftSize = leftSize;
        RightSize = rightSize;
    }

    private static string BuildMessage(string relativePath, long limit, long? leftSize, long? rightSize)
    {
        var sides = new List<string>();
        if (leftSize is { } left)
        {
            sides.Add($"left side is {left:N0} bytes");
        }

        if (rightSize is { } right)
        {
            sides.Add($"right side is {right:N0} bytes");
        }

        return $"Cannot diff '{relativePath}': {string.Join(" and ", sides)}, exceeding the {limit:N0}-byte diff size limit.";
    }
}

/// <summary>
/// A diff was requested where at least one resolved version's content was detected as binary
/// from a bounded initial sample of its bytes (snapshot-history spec: "Refusing binary
/// content"). Raised before either version's full content is read into memory. Only the side(s)
/// actually detected as binary are reflected in <see cref="LeftIsBinary"/>/<see cref="RightIsBinary"/>
/// and named in the message.
/// </summary>
public sealed class DiffBinaryContentException : Exception
{
    public string RelativePath { get; }
    public bool LeftIsBinary { get; }
    public bool RightIsBinary { get; }

    public DiffBinaryContentException(string relativePath, bool leftIsBinary, bool rightIsBinary)
        : base(BuildMessage(relativePath, leftIsBinary, rightIsBinary))
    {
        RelativePath = relativePath;
        LeftIsBinary = leftIsBinary;
        RightIsBinary = rightIsBinary;
    }

    private static string BuildMessage(string relativePath, bool leftIsBinary, bool rightIsBinary)
    {
        var sides = new List<string>();
        if (leftIsBinary)
        {
            sides.Add("left side");
        }

        if (rightIsBinary)
        {
            sides.Add("right side");
        }

        return $"Cannot diff '{relativePath}': {string.Join(" and ", sides)} appears to be binary content.";
    }
}

/// <summary>
/// A version was requested to be shown whose content was detected as binary from a bounded
/// initial sample of its bytes, while standard output is an interactive terminal and the user
/// did not explicitly force binary output (snapshot-history spec: "Refusing binary content to
/// an interactive terminal"). Raised before any of the content is written to the destination
/// stream. A single-sided sibling of <see cref="DiffBinaryContentException"/>: <c>show</c>
/// resolves exactly one stream, unlike <c>diff</c>'s two named sides.
/// </summary>
public sealed class ShowBinaryContentException(string relativePath)
    : Exception($"Cannot show '{relativePath}': content appears to be binary. Pass --force-binary to show it anyway, or redirect output to a file.")
{
    public string RelativePath { get; } = relativePath;
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
/// A restore was requested for a resolved version that is a symlink/junction entry
/// (<see cref="FileChangeKind.Linked"/>) rather than one with actual stored content -
/// there is nothing to extract.
/// </summary>
public sealed class RestoreLinkedEntryException(string relativePath)
    : Exception($"Cannot restore '{relativePath}': the resolved version is a symlink/junction with no stored content.")
{
    public string RelativePath { get; } = relativePath;
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
