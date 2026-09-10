using Vara.Core.Configuration;

namespace Vara.Application.Profiles;

/// <summary>
/// A mutable, in-progress editable representation of a single <see cref="Profile"/>'s
/// fields, used by the interactive profile editor (<c>vara profiles</c>). Unlike
/// <see cref="Profile"/> itself (an immutable record whose constructor enforces all
/// structural invariants), a <see cref="ProfileDraft"/> can represent a state
/// <see cref="Profile"/>'s constructor would reject (missing name, zero sources, a
/// mid-edit numeric field), so the edit screen always has something to display and edit
/// in any order, from empty (add) through to fully valid (ready to save).
/// </summary>
/// <remarks>
/// "Live validation" (see <see cref="Revalidate"/>) means: after every field change,
/// attempt to construct a <see cref="Profile"/> (and its <see cref="Source"/>s) from the
/// current draft state, reusing the domain constructors as the single source of truth for
/// what is valid, plus the cross-profile <see cref="ProfileNameUniqueness"/> check. On
/// success, the constructed <see cref="Profile"/> is cached as <see cref="CurrentValidProfile"/>
/// and <see cref="CurrentError"/> is cleared. On failure, the last valid snapshot (if any)
/// is left in place, but <see cref="CurrentError"/> is set to the failure's message, so the
/// edit screen can display it immediately rather than only surfacing it on Save.
/// </remarks>
public sealed class ProfileDraft
{
    /// <summary>
    /// The name of the profile this draft was initialized from - <see langword="null"/>
    /// for a brand-new profile, otherwise set once (when the draft is created via
    /// <see cref="FromProfile"/>) and never updated by later edits to <see cref="Name"/>.
    /// This is the anchor Save and the uniqueness check both use to identify which saved
    /// profile (if any) this draft replaces, independent of the draft's current, possibly
    /// just-renamed <see cref="Name"/>.
    /// </summary>
    public string? OriginalName { get; private init; }

    public string? Name { get; set; }
    public string? Target { get; set; }
    public IList<SourceDraft> Sources { get; } = new List<SourceDraft>();

    public bool HasRetention { get; set; }
    public int KeepDaily { get; set; }
    public int KeepWeekly { get; set; }
    public int KeepMonthly { get; set; }
    public int KeepYearly { get; set; }

    public bool HasConcurrency { get; set; }
    public int? ScanConcurrency { get; set; }
    public int? TransferConcurrency { get; set; }

    /// <summary>
    /// The most recently constructed valid <see cref="Profile"/>, kept as the "current
    /// valid snapshot" across a subsequent failed <see cref="Revalidate"/> call - see the
    /// type-level remarks. <see langword="null"/> until the draft has been valid at least
    /// once.
    /// </summary>
    public Profile? CurrentValidProfile { get; private set; }

    /// <summary>
    /// The current validation error, if the draft's last <see cref="Revalidate"/> call
    /// failed; <see langword="null"/> when the draft currently satisfies every rule.
    /// </summary>
    public string? CurrentError { get; private set; }

    /// <summary>Initializes an empty draft for adding a brand-new profile.</summary>
    public static ProfileDraft ForNewProfile() => new();

    /// <summary>Initializes a draft reproducing an existing <see cref="Profile"/>'s values.</summary>
    public static ProfileDraft FromProfile(Profile profile)
    {
        var draft = new ProfileDraft
        {
            OriginalName = profile.Name,
            Name = profile.Name,
            Target = profile.TargetRoot,
        };

        foreach (var source in profile.Sources)
        {
            draft.Sources.Add(SourceDraft.FromSource(source));
        }

        if (profile.Retention is { } retention)
        {
            draft.HasRetention = true;
            draft.KeepDaily = retention.KeepDaily;
            draft.KeepWeekly = retention.KeepWeekly;
            draft.KeepMonthly = retention.KeepMonthly;
            draft.KeepYearly = retention.KeepYearly;
        }

        if (profile.Concurrency is { } concurrency)
        {
            draft.HasConcurrency = true;
            draft.ScanConcurrency = concurrency.ScanConcurrency;
            draft.TransferConcurrency = concurrency.TransferConcurrency;
        }

        return draft;
    }

    /// <summary>
    /// Re-validates the draft against its current field values and the rest of the
    /// currently-saved profiles (<paramref name="existingProfiles"/>), per the type-level
    /// remarks. Returns <see langword="true"/> when the draft is currently valid (in which
    /// case <see cref="CurrentValidProfile"/> reflects it and <see cref="CurrentError"/> is
    /// <see langword="null"/>), or <see langword="false"/> otherwise (in which case
    /// <see cref="CurrentError"/> describes why).
    /// </summary>
    public bool Revalidate(IReadOnlyList<Profile> existingProfiles)
    {
        var sources = new List<Source>(Sources.Count);
        foreach (var sourceDraft in Sources)
        {
            if (!sourceDraft.TryBuild(out var source, out var sourceError))
            {
                CurrentError = sourceError;
                return false;
            }

            sources.Add(source!);
        }

        Profile profile;
        try
        {
            profile = new Profile(
                Name ?? string.Empty,
                Target ?? string.Empty,
                sources,
                HasRetention ? new RetentionPolicy(KeepDaily, KeepWeekly, KeepMonthly, KeepYearly) : null,
                HasConcurrency ? new ConcurrencySettings(ScanConcurrency, TransferConcurrency) : null);
        }
        catch (ArgumentException ex)
        {
            CurrentError = ex.Message;
            return false;
        }

        if (ProfileNameUniqueness.ConflictsWithAnotherProfile(existingProfiles.Select(p => p.Name), profile.Name, OriginalName))
        {
            // Authored for the editor: at this moment the name is used once in the saved
            // configuration file and once in this unsaved draft, which is not the
            // "defined more than once in the configuration file" situation the loader's
            // DuplicateProfileNameException describes - that exception (and its message)
            // stays reserved for the loader's own use, unchanged.
            CurrentError = $"A profile named '{profile.Name}' already exists. Choose a different name to save this profile.";
            return false;
        }

        CurrentValidProfile = profile;
        CurrentError = null;
        return true;
    }
}
