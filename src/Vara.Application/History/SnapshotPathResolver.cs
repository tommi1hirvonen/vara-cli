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
    /// <param name="isWithinMirror">
    /// Returns whether an absolute path resolves inside the mirror root, reparse points
    /// (symlinks/junctions) included - the caller's <c>IContentStore.IsWithinMirror</c>. This is
    /// the single shared implementation of "is this path inside the mirror"; this type no
    /// longer computes its own lexical-only copy of that check.
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
        Func<string, bool> isWithinMirror,
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

        // isWithinMirror is reparse-aware (it may say "yes" for a path reached only through a
        // symlink/junction whose real target lies inside the mirror, even though the literal
        // path string does not start with the mirror root); TryStripMirrorRootPrefix stays
        // purely lexical. When the two disagree - contained, but no literal prefix to strip -
        // fall through to the absolute-source-path interpretation rather than fabricating an
        // incorrect mirror-relative path from a prefix strip that doesn't apply.
        var candidate = isWithinMirror(absolute) && TryStripMirrorRootPrefix(mirrorRoot, absolute, out var mirrorRelative)
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
    /// Whether <paramref name="absolutePath"/> lexically starts with <paramref name="mirrorRoot"/>
    /// (or equals it), and if so, the mirror-relative remainder. Purely a lexical prefix-strip -
    /// intentionally not reparse-aware (see this type's design notes on the "yes but can't strip"
    /// case) - kept separate from the reparse-aware containment decision made via the injected
    /// <c>isWithinMirror</c> predicate in <see cref="TryResolve"/>.
    /// </summary>
    private static bool TryStripMirrorRootPrefix(string mirrorRoot, string absolutePath, out string mirrorRelative)
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
