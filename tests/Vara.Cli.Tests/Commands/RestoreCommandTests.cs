using System.Threading;
using Microsoft.Data.Sqlite;
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

public class RestoreCommandTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-src-{Guid.NewGuid():N}");
    private readonly string _outRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-out-{Guid.NewGuid():N}");
    private readonly XxHash128Hasher _hasher = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }

        if (Directory.Exists(_outRoot))
        {
            Directory.Delete(_outRoot, recursive: true);
        }
    }

    // Neither dependency is ever exercised by most tests below: --recursive/--version and
    // --at/--version mutual exclusion (like --out/--in-place's existing check) is validated
    // by RestoreCommand.Create's parsed-args handler before profile resolution or service
    // construction is ever reached, so a real profile/hasher is unnecessary for those.
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
        RestoreCommand.Create(new ProfileResolver(new UnusedProfileConfigLoader()), new ProfileServiceFactory(new UnusedHasher()));

    private System.CommandLine.Command CreateRealCommand(CancellationToken cancellationToken = default)
    {
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], null);
        return RestoreCommand.Create(
            new ProfileResolver(new SingleProfileConfigLoader(profile)),
            new ProfileServiceFactory(_hasher),
            cancellationToken);
    }

    /// <summary>Stores two files' content in the profile's real content store and records
    /// both as live in a single completed snapshot under "src\", mirroring the shape
    /// <c>PlanDirectoryRestore</c>/<c>ExecuteDirectoryRestore</c> expect.</summary>
    private void SeedTwoTrackedFiles()
    {
        var contentStore = new FileSystemContentStore(_targetRoot, _hasher);
        var (hashA, sizeA) = contentStore.StoreFromStream(new MemoryStream("content a"u8.ToArray()));
        var (hashB, sizeB) = contentStore.StoreFromStream(new MemoryStream("content b"u8.ToArray()));

        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath);
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        repository.RecordFileVersion(snapshot, @"src\a.txt", null, hashA, sizeA, now, FileChangeKind.Added, now);
        repository.RecordFileVersion(snapshot, @"src\b.txt", null, hashB, sizeB, now, FileChangeKind.Added, now);
        repository.CompleteSnapshot(snapshot, now, SnapshotStats.Empty);
    }

    [Fact]
    public void Recursive_combined_with_version_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse(["src", "--recursive", "--version", "5", "--out", @"C:\out"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Recursive_without_version_and_without_out_or_in_place_still_requires_a_destination()
    {
        var command = CreateCommand();

        // --recursive alone doesn't bypass the pre-existing --out/--in-place requirement.
        var exitCode = command.Parse(["src", "--recursive"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void At_combined_with_version_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse(["src", "--at", "2025-01-15", "--version", "5", "--out", @"C:\out"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void A_gracefully_cancelled_recursive_restore_returns_exit_code_partial_failure_and_writes_nothing()
    {
        SeedTwoTrackedFiles();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var command = CreateRealCommand(cts.Token);

        // --force skips the confirmation prompt so the pre-cancelled token's effect - stopping
        // ExecuteDirectoryRestore before its first planned write/removal - is exercised
        // directly, mirroring PruneCommandTests'/CheckCommandTests' own pre-cancelled-token tests.
        var exitCode = command.Parse(["src", "--profile", "test-profile", "--recursive", "--out", _outRoot, "--force"]).Invoke();

        Assert.Equal(ExitCodes.PartialFailure, exitCode);
        Assert.False(File.Exists(Path.Combine(_outRoot, "a.txt")));
        Assert.False(File.Exists(Path.Combine(_outRoot, "b.txt")));
    }

    [Fact]
    public void SelectableVersionsForInteractivePicker_excludes_deleted_and_linked_versions()
    {
        var t0 = DateTimeOffset.UtcNow;
        var added = new FileVersionRecord(1, 1, "a.txt", null, "hash-1", 5, t0, FileChangeKind.Added, t0);
        var deleted = new FileVersionRecord(2, 2, "a.txt", null, "hash-1", 5, t0.AddMinutes(1), FileChangeKind.Deleted, t0.AddMinutes(1));
        var linked = new FileVersionRecord(3, 3, "a.txt", null, null, 0, t0.AddMinutes(2), FileChangeKind.Linked, t0.AddMinutes(2), LinkTarget: @"C:\target");
        var history = new List<FileVersionRecord> { linked, deleted, added };

        var selectable = RestoreCommand.SelectableVersionsForInteractivePicker(history);

        Assert.Single(selectable);
        Assert.Equal(added, selectable[0]);
    }

    [Fact]
    public void SelectableVersionsForInteractivePicker_is_empty_when_every_version_is_deleted_or_linked()
    {
        var t0 = DateTimeOffset.UtcNow;
        var deleted = new FileVersionRecord(1, 1, "link", null, "hash-1", 5, t0, FileChangeKind.Deleted, t0);
        var linked = new FileVersionRecord(2, 2, "link", null, null, 0, t0.AddMinutes(1), FileChangeKind.Linked, t0.AddMinutes(1), LinkTarget: @"C:\target");
        var history = new List<FileVersionRecord> { linked, deleted };

        var selectable = RestoreCommand.SelectableVersionsForInteractivePicker(history);

        Assert.Empty(selectable);
    }
}
