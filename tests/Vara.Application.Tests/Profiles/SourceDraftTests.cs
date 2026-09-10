using Vara.Application.Profiles;
using Vara.Core.Configuration;
using Xunit;

namespace Vara.Application.Tests.Profiles;

public class SourceDraftTests
{
    [Fact]
    public void A_valid_draft_constructs_successfully()
    {
        var draft = new SourceDraft { Path = @"C:\data" };

        var built = draft.TryBuild(out var source, out var error);

        Assert.True(built);
        Assert.NotNull(source);
        Assert.Null(error);
        Assert.Equal(@"C:\data", source!.Path);
    }

    [Fact]
    public void A_relative_path_surfaces_the_domain_constructors_error_text()
    {
        var draft = new SourceDraft { Path = "data" };

        var built = draft.TryBuild(out var source, out var error);

        Assert.False(built);
        Assert.Null(source);
        Assert.NotNull(error);
        Assert.Contains("fully-qualified", error);
    }

    [Fact]
    public void A_built_Sources_Excludes_is_unaffected_by_later_edits_to_the_originating_drafts_list()
    {
        var draft = new SourceDraft { Path = @"C:\data" };
        draft.Excludes.Add("*.tmp");

        var built = draft.TryBuild(out var source, out _);
        Assert.True(built);
        var originalExcludes = source!.Excludes.ToList();

        draft.Excludes.Add("*.log");

        Assert.Equal(originalExcludes, source.Excludes);
        Assert.DoesNotContain("*.log", source.Excludes);
    }

    [Fact]
    public void FromSource_reproduces_the_original_values()
    {
        var original = new Source(@"C:\data", recursive: false, excludes: ["a"], includeGlobs: ["*.txt"], excludeGlobs: ["*.tmp"]);

        var draft = SourceDraft.FromSource(original);

        Assert.Equal(original.Path, draft.Path);
        Assert.Equal(original.Recursive, draft.Recursive);
        Assert.Equal(original.Excludes, draft.Excludes);
        Assert.Equal(original.IncludeGlobs, draft.IncludeGlobs);
        Assert.Equal(original.ExcludeGlobs, draft.ExcludeGlobs);
    }
}
