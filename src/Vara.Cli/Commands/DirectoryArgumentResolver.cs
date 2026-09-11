using Vara.Application.History;
using Vara.Core.Abstractions;
using Vara.Core.FileSystem;

namespace Vara.Cli.Commands;

/// <summary>
/// The outcome of resolving a directory argument: the mirror-relative path to use, and
/// whether that path is the mirror root reached only as a fallback - i.e. the current
/// working directory did not resolve inside the mirror, its source-mapped location has no
/// recorded history, and the mirror root was substituted so the command has *something*
/// browsable/restorable rather than erroring out. Callers use this flag to tell the user
/// the root shown is a fallback, not the directory they actually asked about.
/// </summary>
internal readonly record struct DirectoryResolution(string Path, bool FellBackToMirrorRoot);

/// <summary>
/// Resolves a directory argument for `browse`/`restore --recursive` through the same
/// flexible-path priority chain <see cref="SnapshotPathResolver"/> uses for file paths
/// (literal match, then working-directory-relative, then absolute-source-path transform),
/// adapted for a directory rather than a single tracked file: "has history" becomes "any
/// tracked path - live or deleted - falls under this prefix," since a directory has no
/// history of its own.
///
/// A directory argument of `.` or blank - "my current directory" - gets special treatment
/// when the working directory does not resolve inside the mirror: unlike a real (non-root)
/// path, `.`/blank taken completely literally always denotes a valid directory (the mirror
/// root), so trying that interpretation first would always "succeed" and the
/// absolute-source-path mapping - the interpretation that actually reflects where the user
/// is standing in the source tree - would never get a chance to run. So for that specific
/// input, the source-path mapping is tried first, and the literal mirror-root interpretation
/// is only used as a fallback if the mapped location has no recorded history at all.
/// </summary>
internal static class DirectoryArgumentResolver
{
    public static DirectoryResolution Resolve(string mirrorRoot, string rawInput, ISnapshotRepository repository, Func<string, bool> isWithinMirror, string? currentDirectory = null) =>
        Resolve(mirrorRoot, rawInput, candidate => DirectoryTracked(repository, candidate), isWithinMirror, currentDirectory);

    /// <summary>
    /// Same resolution as the <see cref="ISnapshotRepository"/> overload, but takes the
    /// "is this candidate mirror-relative directory tracked" predicate directly, so a caller
    /// with its own notion of "tracked" (for example <c>restore --recursive</c>'s
    /// <c>SnapshotHistoryService.ListDirectory</c>-based check) can reuse this same `.`/blank
    /// handling without going through <see cref="ISnapshotRepository"/>.
    /// </summary>
    public static DirectoryResolution Resolve(string mirrorRoot, string rawInput, Func<string, bool> isDirectoryTracked, Func<string, bool> isWithinMirror, string? currentDirectory = null)
    {
        var cwd = currentDirectory ?? Directory.GetCurrentDirectory();

        string? absolute;
        try
        {
            absolute = Path.GetFullPath(rawInput, cwd);
        }
        catch (ArgumentException)
        {
            absolute = null;
        }

        var cwdInsideMirror = absolute != null && isWithinMirror(absolute);

        if (IsRootOrBlank(rawInput) && !cwdInsideMirror)
        {
            // The literal ("root") interpretation is trivially always valid, so it is tried
            // last here, not first - the reverse of the general order below - specifically so
            // the source-mapped interpretation (reflecting where the user is actually standing
            // in the source tree) gets first refusal.
            if (absolute != null)
            {
                var mapped = AbsolutePathMirrorMapper.ToMirrorPath(absolute);
                if (isDirectoryTracked(mapped))
                {
                    return new DirectoryResolution(mapped, FellBackToMirrorRoot: false);
                }
            }

            return new DirectoryResolution(rawInput, FellBackToMirrorRoot: true);
        }

        // Outside the `.`/blank-while-outside-the-mirror special case, the mirror root is
        // always a valid literal match (as it always has been), and every other candidate is
        // only a match when it is genuinely tracked.
        bool HasHistoryOrIsRoot(string candidate) => IsRootOrBlank(candidate) || isDirectoryTracked(candidate);

        var resolved = SnapshotPathResolver.TryResolve(mirrorRoot, rawInput, HasHistoryOrIsRoot, isWithinMirror, out var resolvedPath, cwd)
            ? resolvedPath
            : rawInput;

        return new DirectoryResolution(resolved, FellBackToMirrorRoot: false);
    }

    private static bool DirectoryTracked(ISnapshotRepository repository, string candidate)
    {
        var prefix = NormalizePrefix(candidate);
        return repository.GetCurrentState().Keys.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || repository.GetTombstones(null).Any(r => r.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRootOrBlank(string path) => NormalizePrefix(path).Length == 0;

    private static string NormalizePrefix(string path)
    {
        var trimmed = path.Trim().Trim('\\', '/');
        return trimmed.Length == 0 || trimmed == "." ? string.Empty : trimmed + "\\";
    }
}
