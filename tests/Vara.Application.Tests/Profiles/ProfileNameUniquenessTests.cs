using Vara.Application.Profiles;
using Xunit;

namespace Vara.Application.Tests.Profiles;

public class ProfileNameUniquenessTests
{
    [Fact]
    public void No_conflict_on_an_empty_list()
    {
        Assert.False(ProfileNameUniqueness.ConflictsWithAnotherProfile([], "files", excludedName: null));
    }

    [Fact]
    public void Conflict_against_a_different_profile_is_case_insensitive()
    {
        Assert.True(ProfileNameUniqueness.ConflictsWithAnotherProfile(["FILES"], "files", excludedName: null));
    }

    [Fact]
    public void No_conflict_when_the_candidate_name_equals_the_replaced_profiles_own_name()
    {
        Assert.False(ProfileNameUniqueness.ConflictsWithAnotherProfile(["files"], "files", excludedName: "files"));
    }

    [Fact]
    public void Conflict_when_the_candidate_name_equals_a_different_profiles_name_even_if_it_also_equals_the_replaced_profiles_old_name_coincidentally()
    {
        // Two entries share the name "files" (a defensive edge case for the helper itself -
        // the loader never allows this to happen in a real saved file). Excluding the
        // replaced profile must only ever skip the first matching entry, so the second,
        // genuinely different "files" profile is still reported as a conflict.
        Assert.True(ProfileNameUniqueness.ConflictsWithAnotherProfile(["files", "files"], "files", excludedName: "files"));
    }

    [Fact]
    public void Conflict_against_a_different_profile_when_editing_a_third_profile()
    {
        Assert.True(ProfileNameUniqueness.ConflictsWithAnotherProfile(["files", "photos"], "photos", excludedName: "files"));
    }
}
