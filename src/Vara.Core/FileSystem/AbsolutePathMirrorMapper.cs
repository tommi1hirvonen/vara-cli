namespace Vara.Core.FileSystem;

/// <summary>
/// Converts a full absolute source path into the path it occupies under the mirror
/// target, so that entries from different sources can never collide at the same
/// mirror location (see the mirror-absolute-source-paths change's design.md). Lives
/// in <c>Vara.Core</c> rather than <c>Vara.Infrastructure</c> because it is a pure,
/// dependency-free string transform needed by both <c>Vara.Infrastructure</c> (the
/// backup scanner) and <c>Vara.Application</c> (the snapshot-history capability's
/// flexible path resolution and restore's <c>--in-place</c> mode) - see the
/// browse-and-restore-ux change's design.md.
/// </summary>
public static class AbsolutePathMirrorMapper
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

    /// <summary>
    /// Reverses <see cref="ToMirrorPath"/>: maps a mirror-relative path back to the
    /// original absolute source path it was derived from, by re-inserting <c>:</c>
    /// after a single-character leading path segment (for example,
    /// <c>C\Users\john\file.txt</c> becomes <c>C:\Users\john\file.txt</c>). A path
    /// whose leading segment is not a single character (i.e. was never produced by
    /// <see cref="ToMirrorPath"/>) is returned unchanged.
    /// </summary>
    public static string FromMirrorPath(string mirrorRelativePath)
    {
        var separatorIndex = mirrorRelativePath.IndexOfAny(['\\', '/']);
        var firstSegmentLength = separatorIndex < 0 ? mirrorRelativePath.Length : separatorIndex;

        if (firstSegmentLength == 1)
        {
            return string.Concat(mirrorRelativePath.AsSpan(0, 1), ":", mirrorRelativePath.AsSpan(1));
        }

        return mirrorRelativePath;
    }
}
