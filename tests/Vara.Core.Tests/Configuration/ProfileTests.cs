using Vara.Core.Configuration;
using Xunit;

namespace Vara.Core.Tests.Configuration;

public class ProfileTests
{
    private static Source ValidSource() => new(@"C:\data");

    [Fact]
    public void Constructing_a_valid_profile_succeeds()
    {
        var profile = new Profile("files", @"D:\backup", [ValidSource()], retention: null);

        Assert.Equal("files", profile.Name);
        Assert.Equal(@"D:\backup", profile.TargetRoot);
        Assert.Single(profile.Sources);
        Assert.Null(profile.Retention);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructing_with_a_missing_name_throws(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Profile(name!, @"D:\backup", [ValidSource()], null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructing_with_a_missing_target_throws(string? target)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Profile("files", target!, [ValidSource()], null));
    }

    [Fact]
    public void Constructing_with_zero_sources_throws()
    {
        Assert.Throws<ArgumentException>(() => new Profile("files", @"D:\backup", [], null));
    }

    [Fact]
    public void Constructing_with_null_sources_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new Profile("files", @"D:\backup", null!, null));
    }
}

public class SourceTests
{
    [Fact]
    public void Constructing_a_valid_source_defaults_recursive_to_true()
    {
        var source = new Source(@"C:\data");

        Assert.True(source.Recursive);
        Assert.Empty(source.Excludes);
        Assert.Empty(source.IncludeGlobs);
        Assert.Empty(source.ExcludeGlobs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructing_with_a_missing_path_throws(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Source(path!));
    }

    [Fact]
    public void Non_default_recursive_and_excludes_are_preserved()
    {
        var source = new Source(@"C:\data", recursive: false, excludes: ["node_modules"]);

        Assert.False(source.Recursive);
        Assert.Equal(["node_modules"], source.Excludes);
    }
}

public class RetentionPolicyTests
{
    [Fact]
    public void Constructing_with_non_negative_counts_succeeds()
    {
        var policy = new RetentionPolicy(14, 8, 12, 3);

        Assert.Equal(14, policy.KeepDaily);
        Assert.Equal(8, policy.KeepWeekly);
        Assert.Equal(12, policy.KeepMonthly);
        Assert.Equal(3, policy.KeepYearly);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    public void Constructing_with_a_negative_count_throws(int daily, int weekly, int monthly, int yearly)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionPolicy(daily, weekly, monthly, yearly));
    }
}
