using Microsoft.Data.Sqlite;
using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Infrastructure.Hashing;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class SnapshotsCommandTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-src-{Guid.NewGuid():N}");
    private readonly XxHash128Hasher _hasher = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    private sealed class TrackingProfileConfigLoader(Profile profile) : IProfileConfigLoader
    {
        public List<string> LoadedPaths { get; } = [];

        public IReadOnlyList<Profile> LoadProfiles(string configPath)
        {
            LoadedPaths.Add(configPath);
            return [profile];
        }

        public string DefaultConfigPath => "default-config.yml";
    }

    [Fact]
    public void Explicit_profile_argument_resolves_profile_by_name()
    {
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], null);
        var loader = new TrackingProfileConfigLoader(profile);
        var command = SnapshotsCommand.Create(
            new ProfileResolver(loader),
            new ProfileServiceFactory(_hasher));

        var exitCode = command.Parse(["test-profile"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.Single(loader.LoadedPaths);
        Assert.Equal("default-config.yml", loader.LoadedPaths[0]);
    }

    [Fact]
    public void Omitting_profile_argument_passes_null_and_triggers_cwd_detection()
    {
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], null);
        var loader = new TrackingProfileConfigLoader(profile);
        var command = SnapshotsCommand.Create(
            new ProfileResolver(loader),
            new ProfileServiceFactory(_hasher));

        // When profile is omitted (null), ResolveForBrowsing does not query the config loader
        // and instead attempts CWD detection. Outside any target root, this results in exit code 1
        // (ProfileNameRequiredException).
        var exitCode = command.Parse([]).Invoke();

        Assert.Equal(1, exitCode);
        Assert.Empty(loader.LoadedPaths);
    }

    [Fact]
    public void Passing_profile_option_fails_as_unrecognized_option()
    {
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], null);
        var loader = new TrackingProfileConfigLoader(profile);
        var command = SnapshotsCommand.Create(
            new ProfileResolver(loader),
            new ProfileServiceFactory(_hasher));

        var parseResult = command.Parse(["--profile", "test-profile"]);
        Assert.NotEmpty(parseResult.Errors);
        var exitCode = parseResult.Invoke();
        Assert.NotEqual(0, exitCode);
    }
}
