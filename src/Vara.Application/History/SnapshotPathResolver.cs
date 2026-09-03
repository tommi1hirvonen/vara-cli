using Vara.Core.FileSystem;

namespace Vara.Application.History;

/// <summary>
/// Resolves a path argument typed by a user - in any of the forms the snapshot-history
/// capability's "Flexible path input" requirement accepts - to the mirror-relative path
/// it should be looked up as. Tried in order, the first interpretation that the caller's
/// <c>hasHistory</c> predicate recognizes wins:
/// <list type="number">
/// <item>the input taken completely literally (today's only supported form, so existing
/// callers see no behavior change);</item>
/// <item>the input resolved against the current working directory - if that resolves
/// inside the mirror root, the mirror-relative remainder;</item>
/// <item>otherwise, the input resolved against the current working directory and
/// converted from an absolute source path to its mirror-relative form, the same way the
/// backup pipeline derives mirror paths from source paths (<see cref="AbsolutePathMirrorMapper"/>).</item>
/// </list>
/// </summary>
public static class SnapshotPathResolver
{
    /// <summary>
    /// Attempts to resolve <paramref name="rawInput"/> to a mirror-relative path recognized
    /// by <paramref name="hasHistory"/>.
    /// </summary>
    /// <param name="mirrorRoot">The profile's target root (live mirror root).</param>
    /// <param name="rawInput">The path argument exactly as the user typed it.</param>
    /// <param name="hasHistory">
    /// Returns whether a candidate mirror-relative path has any recorded history - typically
    /// backed by <c>ISnapshotRepository.GetFileHistory(candidate).Count > 0</c>.
    /// </param>
    /// <param name="resolvedPath">
    /// The resolved mirror-relative path, when this method returns <c>true</c>; otherwise
    /// <see cref="string.Empty"/>.
    /// </param>
    /// <param name="currentDirectory">
    /// The directory to resolve a relative <paramref name="rawInput"/> against; defaults to
    /// <see cref="Directory.GetCurrentDirectory"/>. Overridable so callers (and tests) don't
    /// depend on the process's actual working directory.
    /// </param>
    public static bool TryResolve(
        string mirrorRoot,
        string rawInput,
        Func<string, bool> hasHistory,
        out string resolvedPath,
        string? currentDirectory = null)
    {
        // (1) Literal match - preserves today's exact behavior for every existing caller.
        if (hasHistory(rawInput))
        {
            resolvedPath = rawInput;
            return true;
        }

        var cwd = currentDirectory ?? Directory.GetCurrentDirectory();

        string absolute;
        try
        {
            absolute = Path.GetFullPath(rawInput, cwd);
        }
        catch (ArgumentException)
        {
            // Not a well-formed filesystem path (invalid characters, etc.) - only the
            // literal interpretation above could ever have matched.
            resolvedPath = string.Empty;
            return false;
        }

        var candidate = IsWithinMirror(mirrorRoot, absolute, out var mirrorRelative)
            ? mirrorRelative
            : AbsolutePathMirrorMapper.ToMirrorPath(absolute);

        if (hasHistory(candidate))
        {
            resolvedPath = candidate;
            return true;
        }

        resolvedPath = string.Empty;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="absolutePath"/> falls within <paramref name="mirrorRoot"/>, and
    /// if so, the mirror-relative remainder. Mirrors the ordinal, case-insensitive,
    /// trailing-separator-safe prefix check <c>FileSystemContentStore.IsWithinMirror</c>
    /// already uses (see that type's doc comments) - duplicated here in small form rather
    /// than shared, since <c>Vara.Application</c> does not depend on <c>Vara.Infrastructure</c>.
    /// </summary>
    private static bool IsWithinMirror(string mirrorRoot, string absolutePath, out string mirrorRelative)
    {
        var resolvedMirrorRoot = Path.GetFullPath(mirrorRoot);
        var mirrorRootWithSeparator = resolvedMirrorRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedMirrorRoot
            : resolvedMirrorRoot + Path.DirectorySeparatorChar;

        if (absolutePath.Equals(resolvedMirrorRoot, StringComparison.OrdinalIgnoreCase))
        {
            mirrorRelative = string.Empty;
            return true;
        }

        if (absolutePath.StartsWith(mirrorRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            mirrorRelative = absolutePath[mirrorRootWithSeparator.Length..];
            return true;
        }

        mirrorRelative = string.Empty;
        return false;
    }
}
