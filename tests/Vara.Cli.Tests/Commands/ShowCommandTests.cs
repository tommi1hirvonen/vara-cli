using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;
using Xunit;

namespace Vara.Cli.Tests.Commands;

/// <summary>
/// Covers <see cref="ShowCommand"/>'s wiring against a real profile-scoped repository and
/// content store (via the real <see cref="ProfileServiceFactory"/>). The binary-content
/// refusal itself (when standard output is an interactive terminal and <c>--force-binary</c>
/// is not given) can't be driven from an in-process test: this test host's
/// <c>Console.IsOutputRedirected</c> is always <see langword="true"/> (as in CI), so the
/// command always takes the "redirected, stream regardless" branch - that refusal path is
/// instead covered directly at the <c>SnapshotHistoryService.ShowVersion</c> level (see
/// <c>SnapshotHistoryServiceTests</c>'s <c>ShowVersion_with_binary_content_and_forceBinary_false_*</c>
/// case), and manually per this change's tasks.md.
/// </summary>
public class ShowCommandTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-src-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    // Neither dependency is ever exercised by the test below: --at/--version mutual
    // exclusion is validated by ShowCommand.Create's parsed-args handler before profile
    // resolution or service construction is ever reached, so a real profile/hasher is
    // unnecessary here.
    private sealed class UnusedProfileConfigLoader : IProfileConfigLoader
    {
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => throw new NotSupportedException();
        public string DefaultConfigPath => throw new NotSupportedException();
    }

    private sealed class UnusedHasher : IHasher
    {
        public string ComputeHash(Stream content) => throw new NotSupportedException();
    }

    private sealed class SingleProfileConfigLoader(Profile profile) : IProfileConfigLoader
    {
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => [profile];
        public string DefaultConfigPath => "unused";
    }

    private static System.CommandLine.Command CreateCommand() =>
        ShowCommand.Create(new ProfileResolver(new UnusedProfileConfigLoader()), new ProfileServiceFactory(new UnusedHasher()));

    [Fact]
    public void At_combined_with_version_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse(["src", "--at", "2025-01-15", "--version", "5"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    /// <summary>Seeds one recorded file version whose content is binary (contains a NUL byte
    /// within the leading sample) directly into the profile's manifest and content store.</summary>
    private long SeedBinaryVersion()
    {
        var hasher = new XxHash128Hasher();
        var contentStore = new FileSystemContentStore(_targetRoot, hasher);
        var content = new byte[20];
        Array.Fill(content, (byte)'x');
        content[5] = 0;
        var (hash, size) = contentStore.StoreFromStream(new MemoryStream(content));

        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath);
        var t0 = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(snapshot, "a.bin", null, hash, size, t0, FileChangeKind.Added, t0);
        return repository.GetFileHistory("a.bin").Single().Id;
    }

    [Fact]
    public void Binary_content_streams_when_output_is_redirected_without_force_binary()
    {
        var versionId = SeedBinaryVersion();
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], retention: null);
        var command = ShowCommand.Create(
            new ProfileResolver(new SingleProfileConfigLoader(profile)),
            new ProfileServiceFactory(new XxHash128Hasher()));

        // This test host's stdout is redirected (as in CI), so ShowCommand computes
        // forceBinary as true regardless of --force-binary, and streams rather than refusing.
        var exitCode = command.Parse(["a.bin", "--profile", "test-profile", "--version", versionId.ToString()]).Invoke();

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Binary_content_streams_when_force_binary_is_given()
    {
        var versionId = SeedBinaryVersion();
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], retention: null);
        var command = ShowCommand.Create(
            new ProfileResolver(new SingleProfileConfigLoader(profile)),
            new ProfileServiceFactory(new XxHash128Hasher()));

        var exitCode = command.Parse(["a.bin", "--profile", "test-profile", "--version", versionId.ToString(), "--force-binary"]).Invoke();

        Assert.Equal(0, exitCode);
    }
}

