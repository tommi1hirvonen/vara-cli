namespace Vara.Application.Profiles;

/// <summary>
/// The cross-profile "no two profiles share a name" rule, shared by the configuration
/// loader (which enforces it across an entire freshly-parsed file) and the interactive
/// profile editor's live validation (which enforces it for a single in-progress draft
/// against the rest of the already-saved profiles).
/// </summary>
public static class ProfileNameUniqueness
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="candidateName"/> (compared
    /// case-insensitively) matches the name of any profile in <paramref name="profileNames"/>
    /// other than the one being replaced.
    /// </summary>
    /// <param name="excludedName">
    /// The name of the profile <paramref name="candidateName"/> is replacing (the draft's
    /// <c>OriginalName</c> for an edit, or <see langword="null"/> for a brand-new profile).
    /// Only the <em>first</em> entry in <paramref name="profileNames"/> matching
    /// <paramref name="excludedName"/> is skipped - so a candidate name that collides with a
    /// second, genuinely different profile that happens to share that same name is still
    /// reported as a conflict, rather than every same-named entry being excluded.
    /// </param>
    public static bool ConflictsWithAnotherProfile(IEnumerable<string> profileNames, string candidateName, string? excludedName)
    {
        var hasExcludedTheReplacedProfile = false;

        foreach (var name in profileNames)
        {
            if (!hasExcludedTheReplacedProfile && excludedName is not null
                && string.Equals(name, excludedName, StringComparison.OrdinalIgnoreCase))
            {
                hasExcludedTheReplacedProfile = true;
                continue;
            }

            if (string.Equals(name, candidateName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
