namespace Vara.Application.History;

/// <summary>
/// Mirror-relative path matching shared by the history services: a user-supplied directory is
/// normalized to a trailing-separator prefix, then tracked paths are matched against it one
/// whole segment at a time.
/// </summary>
internal static class HistoryPaths
{
    /// <summary>
    /// Normalizes a user-supplied directory path to the trailing-separator-terminated prefix
    /// used to match tracked paths against it - <c>""</c> for the mirror root (empty, <c>.</c>,
    /// or whitespace-only input), otherwise the trimmed path with exactly one trailing
    /// backslash appended, so prefix matching only ever matches whole path segments.
    /// </summary>
    public static string NormalizeDirectoryPrefix(string directoryPath)
    {
        var trimmed = directoryPath.Trim().Trim('\\', '/');
        return trimmed.Length == 0 || trimmed == "." ? string.Empty : trimmed + "\\";
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> falls under <paramref name="prefix"/> (as
    /// produced by <see cref="NormalizeDirectoryPrefix"/>), and if so, its immediate child
    /// segment's name and whether that segment is the path's final one (a file directly in
    /// the listed directory) or an intermediate one (a subdirectory to synthesize).
    /// </summary>
    public static bool TryGetImmediateChild(string relativePath, string prefix, out string name, out bool isLeaf)
    {
        name = string.Empty;
        isLeaf = false;

        string remainder;
        if (prefix.Length == 0)
        {
            remainder = relativePath;
        }
        else
        {
            if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            remainder = relativePath[prefix.Length..];
        }

        if (remainder.Length == 0)
        {
            return false;
        }

        var separatorIndex = remainder.IndexOfAny(['\\', '/']);
        if (separatorIndex < 0)
        {
            name = remainder;
            isLeaf = true;
            return true;
        }

        name = remainder[..separatorIndex];
        isLeaf = false;
        return name.Length > 0;
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> falls under <paramref name="prefix"/> (as
    /// produced by <see cref="NormalizeDirectoryPrefix"/>), at any depth - unlike
    /// <see cref="TryGetImmediateChild"/>, which only matches the prefix's immediate children.
    /// Used by directory restore, which needs every descendant, not just one level. When it
    /// falls under the prefix, <paramref name="subPath"/> is the portion of
    /// <paramref name="relativePath"/> after the prefix - the path's location relative to the
    /// requested directory, used to compute a directory restore's destination.
    /// </summary>
    public static bool TryGetDescendantSubPath(string relativePath, string prefix, out string subPath)
    {
        if (prefix.Length == 0)
        {
            subPath = relativePath;
            return true;
        }

        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            subPath = string.Empty;
            return false;
        }

        subPath = relativePath[prefix.Length..];
        return true;
    }
}
