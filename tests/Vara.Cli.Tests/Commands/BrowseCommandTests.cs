using Spectre.Console.Testing;
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
/// Covers <see cref="BrowseCommand"/>'s directory-argument resolution against a real
/// profile-scoped repository, in particular the snapshot-history delta's "Browsing or
/// recursively restoring the current directory from within a source" and "Current directory
/// outside the mirror has no recorded history" scenarios for `browse .` - mirroring the same
/// console-injection and cwd-manipulation pattern <c>RestoreCommandTests</c> uses for the
/// equivalent `restore --recursive .` scenarios.
/// </summary>
public class BrowseCommandTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-src-{Guid.NewGuid():N}");
    private readonly XxHash128Hasher _hasher = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    private sealed class SingleProfileConfigLoader(Profile profile) : IProfileConfigLoader
    {
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => [profile];
        public string DefaultConfigPath => "unused";
    }

    private System.CommandLine.Command CreateRealCommand(Spectre.Console.IAnsiConsole? console = null)
    {
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], null);
        return BrowseCommand.Create(
            new ProfileResolver(new SingleProfileConfigLoader(profile)),
            new ProfileServiceFactory(_hasher),
            console);
    }

    /// <summary>Stores one file's content and records it as live under "src\", an arbitrary
    /// prefix unrelated to <c>_sourceRoot</c>'s real absolute path - so a cwd of
    /// <c>_sourceRoot</c> resolving `.` through the absolute-source-path mapping does not land
    /// on this tracked prefix, while the mirror root as a whole still has recorded history.</summary>
    private void SeedOneTrackedFile()
    {
        var contentStore = new FileSystemContentStore(_targetRoot, _hasher);
        var (hash, size) = contentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));

        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath);
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, @"src\a.txt", null, hash, size, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);
    }

    /// <summary>Stores one file's content and records it as live under the mirror-relative path
    /// <see cref="Vara.Core.FileSystem.AbsolutePathMirrorMapper"/> derives from the profile's
    /// actual <c>_sourceRoot</c> - so a cwd of <c>_sourceRoot</c> resolving `.` through the
    /// absolute-source-path mapping lands exactly on this tracked prefix.</summary>
    private void SeedOneTrackedFileUnderSourceRoot()
    {
        var contentStore = new FileSystemContentStore(_targetRoot, _hasher);
        var (hash, size) = contentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));

        var mirrorPrefix = Vara.Core.FileSystem.AbsolutePathMirrorMapper.ToMirrorPath(_sourceRoot);

        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath);
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, Path.Combine(mirrorPrefix, "a.txt"), null, hash, size, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);
    }

    [Fact]
    public void Browsing_dot_from_a_source_directory_with_no_tracked_history_falls_back_to_mirror_root_with_a_message()
    {
        SeedOneTrackedFile();
        Directory.CreateDirectory(_sourceRoot);
        var testConsole = new TestConsole();
        var command = CreateRealCommand(testConsole);

        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_sourceRoot);
            var exitCode = command.Parse([".", "--profile", "test-profile"]).Invoke();

            Assert.Equal(0, exitCode);
            Assert.Contains("no recorded history", testConsole.Output);
            // The mirror-root listing shown as a fallback is a one-level (immediate-children)
            // listing, so "src" (the tracked file's containing directory) is what's shown here,
            // not "a.txt" itself.
            Assert.Contains("src", testConsole.Output);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public void Browsing_dot_from_a_source_directory_with_tracked_history_lists_that_directory()
    {
        SeedOneTrackedFileUnderSourceRoot();
        Directory.CreateDirectory(_sourceRoot);
        var testConsole = new TestConsole();
        var command = CreateRealCommand(testConsole);

        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_sourceRoot);
            var exitCode = command.Parse([".", "--profile", "test-profile"]).Invoke();

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("no recorded history", testConsole.Output);
            Assert.Contains("a.txt", testConsole.Output);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }
}
