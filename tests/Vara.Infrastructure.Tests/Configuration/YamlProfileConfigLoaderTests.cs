using Vara.Core.Configuration;
using Vara.Infrastructure.Configuration;
using Xunit;

namespace Vara.Infrastructure.Tests.Configuration;

public class YamlProfileConfigLoaderTests
{
    private readonly YamlProfileConfigLoader _loader = new();

    [Fact]
    public void DefaultConfigPath_points_at_dot_vara_profiles_yml_under_the_user_profile()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vara", "profiles.yml");

        Assert.Equal(expected, _loader.DefaultConfigPath);
    }

    [Fact]
    public void Missing_configuration_file_throws()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profiles.yml");

        var ex = Assert.Throws<ProfileConfigNotFoundException>(() => _loader.LoadProfiles(missingPath));
        Assert.Equal(missingPath, ex.ConfigPath);
    }

    [Fact]
    public void Loading_the_documented_sample_config_produces_the_expected_profiles()
    {
        var samplePath = FindRepoFile("docs", "config-sample.yml");

        var profiles = _loader.LoadProfiles(samplePath);

        Assert.Equal(2, profiles.Count);

        var files = Assert.Single(profiles, p => p.Name == "files");
        Assert.Equal(@"D:\backup\", files.TargetRoot);
        Assert.Equal(2, files.Sources.Count);
        Assert.False(files.Sources[0].Recursive);
        Assert.True(files.Sources[1].Recursive);
        Assert.Equal(["subfolder1\\", "subfolder2\\"], files.Sources[1].Excludes);
        Assert.NotNull(files.Retention);
        Assert.Equal(14, files.Retention!.KeepDaily);
        Assert.Equal(8, files.Retention.KeepWeekly);
        Assert.Equal(12, files.Retention.KeepMonthly);
        Assert.Equal(3, files.Retention.KeepYearly);

        var photos = Assert.Single(profiles, p => p.Name == "photos");
        Assert.Equal(@"D:\photos\", photos.TargetRoot);
        Assert.Equal(2, photos.Sources.Count);
        Assert.Null(photos.Retention);
    }

    [Fact]
    public void Profile_missing_target_field_throws_a_validation_error_naming_the_profile()
    {
        var path = WriteTempConfig(
            """
            profiles:
              - name: broken
                sources:
                  - path: 'C:\data'
            """);

        var ex = Assert.Throws<ProfileValidationException>(() => _loader.LoadProfiles(path));
        Assert.Equal("broken", ex.ProfileName);
    }

    [Fact]
    public void Profile_with_zero_sources_throws_a_validation_error()
    {
        var path = WriteTempConfig(
            """
            profiles:
              - name: broken
                target: 'D:\backup'
                sources: []
            """);

        Assert.Throws<ProfileValidationException>(() => _loader.LoadProfiles(path));
    }

    [Fact]
    public void Source_without_a_recursive_flag_defaults_to_recursive_true()
    {
        var path = WriteTempConfig(
            """
            profiles:
              - name: files
                target: 'D:\backup'
                sources:
                  - path: 'C:\data'
            """);

        var profiles = _loader.LoadProfiles(path);

        Assert.True(profiles[0].Sources[0].Recursive);
    }

    [Fact]
    public void Configuration_file_with_a_non_mapping_root_throws_a_malformed_error()
    {
        var path = WriteTempConfig(
            """
            - just
            - a
            - list
            """);

        var ex = Assert.Throws<ProfileConfigMalformedException>(() => _loader.LoadProfiles(path));
        Assert.Equal(path, ex.ConfigPath);
    }

    [Fact]
    public void Configuration_file_with_profiles_not_a_list_throws_a_malformed_error()
    {
        var path = WriteTempConfig(
            """
            profiles:
              name: files
              target: 'D:\backup'
            """);

        var ex = Assert.Throws<ProfileConfigMalformedException>(() => _loader.LoadProfiles(path));
        Assert.Equal(path, ex.ConfigPath);
    }

    [Fact]
    public void Empty_configuration_file_produces_zero_profiles_without_throwing()
    {
        var path = WriteTempConfig(
            """
            # just a comment, no documents
            """);

        var profiles = _loader.LoadProfiles(path);

        Assert.Empty(profiles);
    }

    [Fact]
    public void Configuration_file_without_a_profiles_key_produces_zero_profiles_without_throwing()
    {
        var path = WriteTempConfig(
            """
            some_other_key: value
            """);

        var profiles = _loader.LoadProfiles(path);

        Assert.Empty(profiles);
    }

    [Fact]
    public void Duplicate_profile_names_throw()
    {
        var path = WriteTempConfig(
            """
            profiles:
              - name: files
                target: 'D:\backup'
                sources:
                  - path: 'C:\data'
              - name: files
                target: 'D:\other'
                sources:
                  - path: 'C:\other-data'
            """);

        var ex = Assert.Throws<DuplicateProfileNameException>(() => _loader.LoadProfiles(path));
        Assert.Equal("files", ex.ProfileName);
    }

    [Fact]
    public void Retention_field_with_a_non_numeric_value_throws_a_validation_error_naming_the_field()
    {
        var path = WriteTempConfig(
            """
            profiles:
              - name: broken
                target: 'D:\backup'
                sources:
                  - path: 'C:\data'
                retention:
                  keep_daily: abc
            """);

        var ex = Assert.Throws<ProfileValidationException>(() => _loader.LoadProfiles(path));
        Assert.Equal("broken", ex.ProfileName);
        Assert.Contains("keep_daily", ex.Reason);
        Assert.Contains("abc", ex.Reason);
    }

    [Fact]
    public void Retention_field_with_a_negative_value_throws_a_validation_error_naming_the_field()
    {
        var path = WriteTempConfig(
            """
            profiles:
              - name: broken
                target: 'D:\backup'
                sources:
                  - path: 'C:\data'
                retention:
                  keep_weekly: -1
            """);

        var ex = Assert.Throws<ProfileValidationException>(() => _loader.LoadProfiles(path));
        Assert.Equal("broken", ex.ProfileName);
        Assert.Contains("keep_weekly", ex.Reason);
        Assert.Contains("-1", ex.Reason);
    }

    [Fact]
    public void Retention_fields_with_zero_values_load_successfully()
    {
        var path = WriteTempConfig(
            """
            profiles:
              - name: files
                target: 'D:\backup'
                sources:
                  - path: 'C:\data'
                retention:
                  keep_daily: 0
                  keep_weekly: 0
                  keep_monthly: 0
                  keep_yearly: 0
            """);

        var profiles = _loader.LoadProfiles(path);

        var retention = profiles[0].Retention;
        Assert.NotNull(retention);
        Assert.Equal(0, retention!.KeepDaily);
        Assert.Equal(0, retention.KeepWeekly);
        Assert.Equal(0, retention.KeepMonthly);
        Assert.Equal(0, retention.KeepYearly);
    }

    private static string WriteTempConfig(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vara-test-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private static string FindRepoFile(params string[] relativeSegments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate '{Path.Combine(relativeSegments)}' by walking up from '{AppContext.BaseDirectory}'.");
    }
}
