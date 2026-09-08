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
        Assert.Null(profile.Concurrency);
    }

    [Fact]
    public void Constructing_a_profile_with_concurrency_configured_preserves_it()
    {
        var concurrency = new ConcurrencySettings(8, 2);

        var profile = new Profile("files", @"D:\backup", [ValidSource()], retention: null, concurrency);

        Assert.Same(concurrency, profile.Concurrency);
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

    [Fact]
    public void Constructing_with_a_target_equal_to_a_source_throws()
    {
        Assert.Throws<ArgumentException>(() => new Profile("files", @"C:\data", [ValidSource()], null));
    }

    [Fact]
    public void Constructing_with_a_target_nested_inside_a_source_throws()
    {
        var source = new Source(@"C:\Users\me");

        Assert.Throws<ArgumentException>(() => new Profile("files", @"C:\Users\me\backup", [source], null));
    }

    [Fact]
    public void Constructing_with_a_source_nested_inside_the_target_throws()
    {
        var source = new Source(@"C:\Users\me\backup\Documents");

        Assert.Throws<ArgumentException>(() => new Profile("files", @"C:\Users\me\backup", [source], null));
    }

    [Fact]
    public void Constructing_with_case_and_trailing_separator_differences_still_detects_overlap()
    {
        var source = new Source(@"C:\Backup");

        Assert.Throws<ArgumentException>(() => new Profile("files", @"c:\backup\", [source], null));
    }

    [Fact]
    public void Constructing_with_mixed_separator_styles_still_detects_target_source_overlap()
    {
        var source = new Source(@"C:\backup\Documents");

        Assert.Throws<ArgumentException>(() => new Profile("files", "C:/backup", [source], null));
    }

    [Fact]
    public void Constructing_with_non_overlapping_target_and_sources_succeeds()
    {
        var profile = new Profile("files", @"D:\backup", [new Source(@"C:\data")], null);

        Assert.Equal(@"D:\backup", profile.TargetRoot);
    }

    [Fact]
    public void Constructing_with_overlap_validation_disabled_allows_target_equal_to_source()
    {
        var source = new Source(@"C:\Users\me");

        var profile = new Profile("files", @"C:\Users\me", [source], null, validateSourceOverlap: false);

        Assert.Equal(@"C:\Users\me", profile.TargetRoot);
    }

    [Theory]
    [InlineData(@"backup")]
    [InlineData(@"backup\")]
    [InlineData(@"..\backup")]
    [InlineData(@".\backup")]
    public void Constructing_with_a_relative_target_root_throws(string target)
    {
        Assert.Throws<ArgumentException>(() => new Profile("files", target, [ValidSource()], null));
    }

    [Fact]
    public void Constructing_with_a_forward_slash_target_root_normalizes_to_the_native_separator()
    {
        var profile = new Profile("files", "D:/backup", [ValidSource()], null);

        Assert.Equal(@"D:\backup", profile.TargetRoot);
        Assert.DoesNotContain('/', profile.TargetRoot);
    }

    [Fact]
    public void Constructing_with_two_identical_source_paths_throws()
    {
        var sources = new[] { new Source(@"C:\data"), new Source(@"C:\data") };

        Assert.Throws<ArgumentException>(() => new Profile("files", @"D:\backup", sources, null));
    }

    [Fact]
    public void Constructing_with_a_source_nested_inside_another_source_throws()
    {
        var sources = new[] { new Source(@"C:\Users\me"), new Source(@"C:\Users\me\Documents") };

        Assert.Throws<ArgumentException>(() => new Profile("files", @"D:\backup", sources, null));
    }

    [Fact]
    public void Constructing_with_sources_differing_only_in_case_and_trailing_separator_throws()
    {
        var sources = new[] { new Source(@"C:\Data\"), new Source(@"c:\data") };

        Assert.Throws<ArgumentException>(() => new Profile("files", @"D:\backup", sources, null));
    }

    [Fact]
    public void Constructing_with_multiple_non_overlapping_sources_succeeds()
    {
        var sources = new[] { new Source(@"C:\data"), new Source(@"C:\other"), new Source(@"E:\stuff") };

        var profile = new Profile("files", @"D:\backup", sources, null);

        Assert.Equal(3, profile.Sources.Count);
    }

    [Fact]
    public void Constructing_with_a_non_adjacent_overlapping_pair_of_sources_throws()
    {
        var sources = new[] { new Source(@"C:\data"), new Source(@"C:\other"), new Source(@"C:\data\nested") };

        Assert.Throws<ArgumentException>(() => new Profile("files", @"D:\backup", sources, null));
    }

    [Fact]
    public void Constructing_with_overlapping_sources_and_source_overlap_validation_disabled_succeeds()
    {
        var sources = new[] { new Source(@"C:\data"), new Source(@"C:\data\nested") };

        var profile = new Profile("files", @"D:\backup", sources, null, validateSourceOverlap: false);

        Assert.Equal(2, profile.Sources.Count);
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

    [Theory]
    [InlineData(@"Documents")]
    [InlineData(@".\src")]
    [InlineData(@"..\src")]
    [InlineData(@"C:data")]
    public void Constructing_with_a_relative_path_throws(string path)
    {
        Assert.Throws<ArgumentException>(() => new Source(path));
    }

    [Fact]
    public void Constructing_with_forward_slashes_normalizes_to_the_native_separator()
    {
        var source = new Source("C:/Users/me/Docs");

        Assert.Equal(@"C:\Users\me\Docs", source.Path);
        Assert.DoesNotContain('/', source.Path);
    }

    [Fact]
    public void Constructing_with_equivalent_paths_differing_only_in_separator_style_normalizes_to_the_same_value()
    {
        var forwardSlash = new Source("C:/Data");
        var nativeSeparator = new Source(@"C:\Data");

        Assert.Equal(forwardSlash.Path, nativeSeparator.Path);
    }

    [Fact]
    public void Constructing_with_a_trailing_separator_preserves_it()
    {
        var withTrailingSeparator = new Source(@"C:\Backups\vara\");
        var withoutTrailingSeparator = new Source(@"C:\Backups\vara");

        Assert.NotEqual(withTrailingSeparator.Path, withoutTrailingSeparator.Path);
        Assert.EndsWith(@"\", withTrailingSeparator.Path);
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

public class ConcurrencySettingsTests
{
    [Fact]
    public void Constructing_with_positive_values_succeeds()
    {
        var settings = new ConcurrencySettings(8, 2);

        Assert.Equal(8, settings.ScanConcurrency);
        Assert.Equal(2, settings.TransferConcurrency);
    }

    [Fact]
    public void Constructing_with_both_values_null_succeeds()
    {
        var settings = new ConcurrencySettings(null, null);

        Assert.Null(settings.ScanConcurrency);
        Assert.Null(settings.TransferConcurrency);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public void Constructing_with_a_non_positive_scan_concurrency_throws(int? scanConcurrency, int? transferConcurrency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrencySettings(scanConcurrency, transferConcurrency));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, -1)]
    public void Constructing_with_a_non_positive_transfer_concurrency_throws(int? scanConcurrency, int? transferConcurrency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrencySettings(scanConcurrency, transferConcurrency));
    }
}
