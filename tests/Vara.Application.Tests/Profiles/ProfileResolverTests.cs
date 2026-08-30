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
}
