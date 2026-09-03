using Vara.Application.Profiles;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Xunit;

namespace Vara.Application.Tests.Profiles;

public class ProfileResolverTests
{
    private static Profile Files => new("files", @"D:\backup", [new Source(@"C:\data")], null);

    private sealed class StubConfigLoader(IReadOnlyList<Profile> profiles) : IProfileConfigLoader
    {
        public string DefaultConfigPath => @"C:\fake\.vara\profiles.yml";
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => profiles;
    }

    [Fact]
    public void Resolving_a_known_profile_name_returns_it_case_insensitively()
    {
        var resolver = new ProfileResolver(new StubConfigLoader([Files]));

        var resolved = resolver.Resolve("FILES");

        Assert.Equal("files", resolved.Name);
    }

    [Fact]
    public void Resolving_an_unknown_profile_name_throws()
    {
        var resolver = new ProfileResolver(new StubConfigLoader([Files]));

        var ex = Assert.Throws<UnknownProfileException>(() => resolver.Resolve("missing"));
        Assert.Equal("missing", ex.ProfileName);
    }

    [Fact]
    public void ResolveForBrowsing_with_an_explicit_name_resolves_via_config_exactly_as_before()
    {
        var resolver = new ProfileResolver(new StubConfigLoader([Files]));

        var resolved = resolver.ResolveForBrowsing("FILES");

        Assert.Equal("files", resolved.Name);
    }

    [Fact]
    public void ResolveForBrowsing_with_no_name_and_a_working_directory_inside_a_target_root_resolves_without_a_config_file()
    {
        var tempRoot = Directory.CreateTempSubdirectory("vara-profile-resolver-tests-");
        try
        {
            var mirrorRoot = tempRoot.FullName;
            Directory.CreateDirectory(Path.Combine(mirrorRoot, ".vara"));
            File.WriteAllBytes(Path.Combine(mirrorRoot, ".vara", "profile.db"), []);
            var nestedWorkingDirectory = Directory.CreateDirectory(Path.Combine(mirrorRoot, "sub", "dir")).FullName;

            // No config loader call should happen at all - a throwing stub proves it.
            var resolver = new ProfileResolver(new ThrowingConfigLoader());

            var resolved = resolver.ResolveForBrowsing(null, startDirectory: nestedWorkingDirectory);

            Assert.Equal(mirrorRoot, resolved.TargetRoot);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveForBrowsing_with_no_name_and_a_working_directory_outside_any_target_root_throws()
    {
        var tempRoot = Directory.CreateTempSubdirectory("vara-profile-resolver-tests-");
        try
        {
            var resolver = new ProfileResolver(new StubConfigLoader([Files]));

            Assert.Throws<ProfileNameRequiredException>(
                () => resolver.ResolveForBrowsing(null, startDirectory: tempRoot.FullName));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    private sealed class ThrowingConfigLoader : IProfileConfigLoader
    {
        public string DefaultConfigPath => throw new InvalidOperationException("Config loader should not be consulted.");
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => throw new InvalidOperationException("Config loader should not be consulted.");
    }
}
