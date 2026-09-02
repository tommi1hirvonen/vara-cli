namespace Vara.Infrastructure.FileSystem;

/// <summary>
/// Converts a full absolute source path into the path it occupies under the mirror
/// target, so that entries from different sources can never collide at the same
/// mirror location (see the mirror-absolute-source-paths change's design.md).
/// </summary>
internal static class AbsolutePathMirrorMapper
{
    /// <summary>
    /// Maps <paramref name="absolutePath"/> to its mirror-relative form by stripping the
    /// colon after a drive letter and leaving every other path segment unchanged (for
    /// example, <c>C:\Users\john\file.txt</c> becomes <c>C\Users\john\file.txt</c>).
    /// Only drive-letter absolute paths are supported; UNC paths are out of scope.
    /// </summary>
    public static string ToMirrorPath(string absolutePath)
    {
        if (absolutePath.Length >= 2 && absolutePath[1] == ':')
        {
            return string.Concat(absolutePath.AsSpan(0, 1), absolutePath.AsSpan(2));
        }

        return absolutePath;
    }
}
