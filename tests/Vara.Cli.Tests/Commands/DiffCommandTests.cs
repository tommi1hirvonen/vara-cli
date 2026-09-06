using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class DiffCommandTests
{
    // Neither dependency is ever exercised by the tests below: --left-at/--left-version
    // and --right-at/--right-version mutual exclusion is validated by DiffCommand.Create's
    // parsed-args handler before profile resolution or service construction is ever
    // reached, so a real profile/hasher is unnecessary here.
    private sealed class UnusedProfileConfigLoader : IProfileConfigLoader
    {
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => throw new NotSupportedException();
        public string DefaultConfigPath => throw new NotSupportedException();
    }

    private sealed class UnusedHasher : IHasher
    {
        public string ComputeHash(Stream content) => throw new NotSupportedException();
    }

    private static System.CommandLine.Command CreateCommand() =>
        DiffCommand.Create(new ProfileResolver(new UnusedProfileConfigLoader()), new ProfileServiceFactory(new UnusedHasher()));

    [Fact]
    public void Left_at_combined_with_left_version_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse([
            "src", "--left-at", "2025-01-15", "--left-version", "5", "--right-at", "2025-01-16",
        ]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Right_at_combined_with_right_version_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse([
            "src", "--left-at", "2025-01-15", "--right-at", "2025-01-16", "--right-version", "5",
        ]).Invoke();

        Assert.Equal(1, exitCode);
    }
}
