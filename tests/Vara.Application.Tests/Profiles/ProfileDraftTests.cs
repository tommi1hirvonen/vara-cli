using Vara.Application.Profiles;
using Vara.Core.Configuration;
using Xunit;

namespace Vara.Application.Tests.Profiles;

public class ProfileDraftTests
{
    [Fact]
    public void An_empty_new_draft_fails_validation_with_a_name_required_style_message()
    {
        var draft = ProfileDraft.ForNewProfile();

        var valid = draft.Revalidate([]);

        Assert.False(valid);
        Assert.Null(draft.CurrentValidProfile);
        Assert.NotNull(draft.CurrentError);
    }

    [Fact]
    public void A_draft_with_a_name_and_target_but_zero_sources_fails_with_an_at_least_one_source_message()
    {
        var draft = ProfileDraft.ForNewProfile();
        draft.Name = "files";
        draft.Target = @"D:\backup";

        var valid = draft.Revalidate([]);

        Assert.False(valid);
        Assert.Contains("at least one source", draft.CurrentError);
    }

    private static ProfileDraft ValidNewDraft()
    {
        var draft = ProfileDraft.ForNewProfile();
        draft.Name = "files";
        draft.Target = @"D:\backup";
        draft.Sources.Add(new SourceDraft { Path = @"C:\data" });
        return draft;
    }

    [Fact]
    public void A_fully_valid_draft_constructs_successfully()
    {
        var draft = ValidNewDraft();

        var valid = draft.Revalidate([]);

        Assert.True(valid);
        Assert.Null(draft.CurrentError);
        Assert.NotNull(draft.CurrentValidProfile);
        Assert.Equal("files", draft.CurrentValidProfile!.Name);
    }

    [Fact]
    public void FromProfile_reproduces_the_same_values_and_captures_its_name_as_OriginalName()
    {
        var profile = new Profile(
            "files",
            @"D:\backup",
            [new Source(@"C:\data")],
            new RetentionPolicy(1, 2, 3, 4),
            new ConcurrencySettings(8, 2));

        var draft = ProfileDraft.FromProfile(profile);

        Assert.Equal("files", draft.OriginalName);
        Assert.Equal("files", draft.Name);
        Assert.Equal(@"D:\backup", draft.Target);
        Assert.Single(draft.Sources);
        Assert.Equal(@"C:\data", draft.Sources[0].Path);
        Assert.True(draft.HasRetention);
        Assert.Equal(1, draft.KeepDaily);
        Assert.True(draft.HasConcurrency);
        Assert.Equal(8, draft.ScanConcurrency);
    }

    [Fact]
    public void Renaming_a_drafts_name_leaves_OriginalName_fixed_to_the_profile_it_started_as()
    {
        var profile = new Profile("files", @"D:\backup", [new Source(@"C:\data")], retention: null);
        var draft = ProfileDraft.FromProfile(profile);

        draft.Name = "documents";

        Assert.Equal("files", draft.OriginalName);
        Assert.Equal("documents", draft.Name);
    }

    [Fact]
    public void Setting_a_sources_path_after_the_target_root_is_set_re_triggers_an_overlap_error()
    {
        var draft = ProfileDraft.ForNewProfile();
        draft.Name = "files";
        draft.Target = @"C:\Users\me\backup";
        var source = new SourceDraft { Path = @"C:\data" };
        draft.Sources.Add(source);
        Assert.True(draft.Revalidate([]));

        // Changing the source's path to overlap the already-set target root.
        source.Path = @"C:\Users\me\backup\Documents";
        var valid = draft.Revalidate([]);

        Assert.False(valid);
        Assert.Contains("overlaps", draft.CurrentError);
    }

    [Fact]
    public void Setting_the_target_root_after_a_source_is_already_present_re_triggers_the_same_overlap_check()
    {
        var draft = ProfileDraft.ForNewProfile();
        draft.Name = "files";
        draft.Sources.Add(new SourceDraft { Path = @"C:\Users\me" });
        draft.Target = @"D:\backup";
        Assert.True(draft.Revalidate([]));

        // Setting the target root to overlap the already-present source.
        draft.Target = @"C:\Users\me\backup";
        var valid = draft.Revalidate([]);

        Assert.False(valid);
        Assert.Contains("overlaps", draft.CurrentError);
    }

    [Fact]
    public void Setting_a_drafts_name_to_another_saved_profiles_name_produces_the_uniqueness_error()
    {
        var existing = new Profile("photos", @"D:\photos", [new Source(@"C:\photos")], retention: null);
        var draft = ValidNewDraft();
        draft.Name = "photos";

        var valid = draft.Revalidate([existing]);

        Assert.False(valid);
        Assert.Contains("photos", draft.CurrentError);
        Assert.Contains("already exists", draft.CurrentError);
    }

    [Fact]
    public void Renaming_to_the_profiles_own_current_name_does_not_conflict()
    {
        var existing = new Profile("files", @"D:\backup", [new Source(@"C:\data")], retention: null);
        var draft = ProfileDraft.FromProfile(existing);

        var valid = draft.Revalidate([existing]);

        Assert.True(valid);
        Assert.Null(draft.CurrentError);
    }

    [Fact]
    public void A_failed_revalidation_keeps_the_last_valid_snapshot()
    {
        var draft = ValidNewDraft();
        Assert.True(draft.Revalidate([]));
        var lastValid = draft.CurrentValidProfile;

        draft.Target = "relative-path";
        var valid = draft.Revalidate([]);

        Assert.False(valid);
        Assert.NotNull(draft.CurrentError);
        Assert.Same(lastValid, draft.CurrentValidProfile);
    }

    [Fact]
    public void Later_source_edits_do_not_change_an_already_validated_profile()
    {
        var draft = ValidNewDraft();
        Assert.True(draft.Revalidate([]));
        var validatedProfile = draft.CurrentValidProfile!;
        var originalExcludes = validatedProfile.Sources[0].Excludes.ToList();

        // Mutate the originating draft's source exclude list after validation.
        draft.Sources[0].Excludes.Add("*.tmp");

        Assert.Equal(originalExcludes, validatedProfile.Sources[0].Excludes);
        Assert.DoesNotContain("*.tmp", validatedProfile.Sources[0].Excludes);
    }
}
