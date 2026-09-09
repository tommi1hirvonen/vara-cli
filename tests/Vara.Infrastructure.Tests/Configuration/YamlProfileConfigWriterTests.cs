using Vara.Core.Configuration;
using Vara.Infrastructure.Configuration;
using Xunit;

namespace Vara.Infrastructure.Tests.Configuration;

public class YamlProfileConfigWriterTests : IDisposable
{
    private readonly YamlProfileConfigWriter _writer = new();
    private readonly YamlProfileConfigLoader _loader = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"vara-writer-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string ConfigPath => Path.Combine(_tempDir, "profiles.yml");

    [Fact]
    public void Writing_then_loading_round_trips_a_full_profile_field_for_field()
    {
        var original = new Profile(
            "files",
            @"D:\backup",
            [
                new Source(@"C:\data", recursive: false, excludes: ["subfolder1\\", "subfolder2\\"], includeGlobs: ["*.txt"], excludeGlobs: ["*.tmp"]),
                new Source(@"C:\other-data"),
            ],
            new RetentionPolicy(14, 8, 12, 3),
            new ConcurrencySettings(8, 2));

        _writer.WriteProfiles([original], ConfigPath);
        var loaded = _loader.LoadProfiles(ConfigPath);

        var reloaded = Assert.Single(loaded);
        Assert.Equal(original.Name, reloaded.Name);
        Assert.Equal(original.TargetRoot, reloaded.TargetRoot);
        Assert.Equal(original.Sources.Count, reloaded.Sources.Count);
        Assert.Equal(original.Sources[0].Path, reloaded.Sources[0].Path);
        Assert.Equal(original.Sources[0].Recursive, reloaded.Sources[0].Recursive);
        Assert.Equal(original.Sources[0].Excludes, reloaded.Sources[0].Excludes);
        Assert.Equal(original.Sources[0].IncludeGlobs, reloaded.Sources[0].IncludeGlobs);
        Assert.Equal(original.Sources[0].ExcludeGlobs, reloaded.Sources[0].ExcludeGlobs);
        Assert.Equal(original.Sources[1].Path, reloaded.Sources[1].Path);
        Assert.Equal(original.Retention!.KeepDaily, reloaded.Retention!.KeepDaily);
        Assert.Equal(original.Retention.KeepWeekly, reloaded.Retention.KeepWeekly);
        Assert.Equal(original.Retention.KeepMonthly, reloaded.Retention.KeepMonthly);
        Assert.Equal(original.Retention.KeepYearly, reloaded.Retention.KeepYearly);
        Assert.Equal(original.Concurrency!.ScanConcurrency, reloaded.Concurrency!.ScanConcurrency);
        Assert.Equal(original.Concurrency.TransferConcurrency, reloaded.Concurrency.TransferConcurrency);
    }

    [Fact]
    public void Writing_a_profile_without_retention_or_concurrency_round_trips_with_both_null()
    {
        var original = new Profile("photos", @"D:\photos", [new Source(@"C:\photos")], retention: null);

        _writer.WriteProfiles([original], ConfigPath);
        var loaded = _loader.LoadProfiles(ConfigPath);

        var reloaded = Assert.Single(loaded);
        Assert.Null(reloaded.Retention);
        Assert.Null(reloaded.Concurrency);
    }

    [Fact]
    public void Writing_to_a_path_whose_parent_directory_does_not_exist_yet_creates_it()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "does", "not", "exist", "profiles.yml");
        var profile = new Profile("files", @"D:\backup", [new Source(@"C:\data")], retention: null);

        _writer.WriteProfiles([profile], nestedPath);

        Assert.True(File.Exists(nestedPath));
        Assert.Single(_loader.LoadProfiles(nestedPath));
    }

    [Fact]
    public void Writing_to_a_path_that_already_has_a_file_overwrites_it()
    {
        var first = new Profile("files", @"D:\backup", [new Source(@"C:\data")], retention: null);
        var second = new Profile("photos", @"D:\photos", [new Source(@"C:\photos")], retention: null);

        _writer.WriteProfiles([first], ConfigPath);
        _writer.WriteProfiles([second], ConfigPath);

        var loaded = _loader.LoadProfiles(ConfigPath);
        var reloaded = Assert.Single(loaded);
        Assert.Equal("photos", reloaded.Name);
    }

    [Fact]
    public void No_temp_file_is_left_behind_after_a_successful_write()
    {
        var profile = new Profile("files", @"D:\backup", [new Source(@"C:\data")], retention: null);

        _writer.WriteProfiles([profile], ConfigPath);

        var remainingFiles = Directory.GetFiles(_tempDir);
        Assert.Single(remainingFiles);
        Assert.Equal(ConfigPath, remainingFiles[0]);
    }

    [Fact]
    public void A_failure_during_the_swap_leaves_the_original_state_untouched_and_no_temp_file_behind()
    {
        // Force the "target doesn't exist yet" (File.Move) swap branch to fail by making
        // the destination path itself an existing directory - File.Exists(ConfigPath) is
        // false for a directory, so the writer takes the File.Move branch, which then
        // throws because a directory already occupies that path. This simulates a failure
        // between the temp file being written and the atomic swap completing.
        Directory.CreateDirectory(ConfigPath);
        var profile = new Profile("files", @"D:\backup", [new Source(@"C:\data")], retention: null);

        Assert.ThrowsAny<IOException>(() => _writer.WriteProfiles([profile], ConfigPath));

        Assert.True(Directory.Exists(ConfigPath));
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void Writing_an_empty_profile_list_produces_a_file_the_loader_reads_back_as_zero_profiles()
    {
        _writer.WriteProfiles([], ConfigPath);

        var loaded = _loader.LoadProfiles(ConfigPath);

        Assert.Empty(loaded);
    }

    [Theory]
    [InlineData("a:name")]
    [InlineData("a#name")]
    [InlineData("a 'quoted' name")]
    public void Special_yaml_characters_in_a_profile_name_round_trip_correctly(string name)
    {
        var profile = new Profile(name, @"D:\backup", [new Source(@"C:\data")], retention: null);

        _writer.WriteProfiles([profile], ConfigPath);
        var loaded = _loader.LoadProfiles(ConfigPath);

        Assert.Equal(name, Assert.Single(loaded).Name);
    }

    [Theory]
    [InlineData("exclude:me")]
    [InlineData("#exclude")]
    public void Special_yaml_characters_in_a_source_exclude_entry_round_trip_correctly(string excludeEntry)
    {
        var profile = new Profile("files", @"D:\backup", [new Source(@"C:\data", excludes: [excludeEntry])], retention: null);

        _writer.WriteProfiles([profile], ConfigPath);
        var loaded = _loader.LoadProfiles(ConfigPath);

        Assert.Equal([excludeEntry], Assert.Single(loaded).Sources[0].Excludes);
    }
}
