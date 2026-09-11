namespace Vara.Cli.Presentation;

/// <summary>
/// Shortens a path to fit within a fixed character budget for use as a progress task label,
/// per the `progress-reporting` capability's "Restore task labels are bounded to a fixed
/// width" requirement. Drops leading parent directory segments (prefixing the remainder with
/// <c>"..."</c>) before ever falling back to truncating the filename itself, so the most
/// useful part of the path - the filename - stays visible for as long as possible as the
/// budget shrinks. Used to pre-truncate a restore task's label before it is handed to
/// Spectre's progress display, per design.md's "Pre-truncate the label string instead of a
/// custom column" decision: Spectre measures a task description's full, unclipped text before
/// laying out the row, so a long, untruncated path would otherwise starve the progress bar of
/// width during that measurement pass, even though the description itself renders truncated.
/// </summary>
public static class PathLabelTruncator
{
    private const string Ellipsis = "...";

    private static readonly char[] SeparatorChars = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Returns <paramref name="path"/> unchanged if it already fits within
    /// <paramref name="maxLength"/> characters; otherwise returns a shortened form that drops
    /// leading parent directory segments (prefixed with <c>"..."</c>), keeping as many trailing
    /// segments - ending in the filename - as fit. If the filename alone exceeds
    /// <paramref name="maxLength"/>, the filename itself is truncated instead.
    /// </summary>
    public static string Truncate(string path, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (path.Length <= maxLength)
        {
            return path;
        }

        var segments = path.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return TruncateFileName(path, maxLength);
        }

        var fileName = segments[^1];

        // Every result that keeps at least the filename is prefixed with "..." plus a
        // separator (see the final assembly below), so that overhead must be reserved up
        // front - if the filename doesn't fit alongside it, no parent segment can help either,
        // and the filename itself must be shortened instead (see TruncateFileName), with no
        // path-level "..." prefix at all.
        var prefixOverhead = Ellipsis.Length + 1;
        if (fileName.Length + prefixOverhead > maxLength)
        {
            return TruncateFileName(fileName, maxLength);
        }

        var kept = new List<string> { fileName };
        var keptLength = fileName.Length;

        for (var i = segments.Length - 2; i >= 0; i--)
        {
            var segment = segments[i];

            // +1 for the separator joining this segment to the ones already kept.
            var candidateLength = keptLength + 1 + segment.Length;
            if (prefixOverhead + candidateLength > maxLength)
            {
                break;
            }

            kept.Insert(0, segment);
            keptLength = candidateLength;
        }

        var joined = string.Join(Path.DirectorySeparatorChar, kept);
        return Ellipsis + Path.DirectorySeparatorChar + joined;
    }

    private static string TruncateFileName(string fileName, int maxLength)
    {
        if (fileName.Length <= maxLength)
        {
            return fileName;
        }

        if (maxLength <= Ellipsis.Length)
        {
            return fileName[..maxLength];
        }

        var keep = maxLength - Ellipsis.Length;
        return Ellipsis + fileName[^keep..];
    }
}
