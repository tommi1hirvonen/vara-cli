using Vara.Application.History;
using Vara.Core.Abstractions;

namespace Vara.Cli.Commands;

/// <summary>
/// Resolves a directory argument for `browse`/`deleted` through the same flexible-path
/// priority chain <see cref="SnapshotPathResolver"/> uses for file paths (literal match,
/// then working-directory-relative, then absolute-source-path transform), adapted for a
/// directory rather than a single tracked file: "has history" becomes "any tracked path -
/// live or deleted - falls under this prefix," since a directory has no history of its own.
/// </summary>
internal static class DirectoryArgumentResolver
{
    public static string Resolve(string mirrorRoot, string rawInput, ISnapshotRepository repository)
    {
        bool DirectoryTracked(string candidate)
        {
            var prefix = NormalizePrefix(candidate);
            if (prefix.Length == 0)
            {
                // The mirror root itself always "exists" as a directory to browse.
                return true;
            }

            return repository.GetCurrentState().Keys.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                || repository.GetTombstones(null).Any(r => r.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        return SnapshotPathResolver.TryResolve(mirrorRoot, rawInput, DirectoryTracked, out var resolved) ? resolved : rawInput;
    }

    private static string NormalizePrefix(string path)
    {
        var trimmed = path.Trim().Trim('\\', '/');
        return trimmed.Length == 0 || trimmed == "." ? string.Empty : trimmed + "\\";
    }
}
