using Microsoft.Data.Sqlite;
using Spectre.Console.Testing;
using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.FileSystem;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class SnapshotCommandTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-snapshot-cli-{Guid.NewGuid():N}");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-snapshot-src-{Guid.NewGuid():N}");
    private readonly XxHash128Hasher _hasher = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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

    private (System.CommandLine.Command Command, TestConsole Console) CreateCommand()
    {
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], null);
        var console = new TestConsole();
        return (
            SnapshotCommand.Create(new ProfileResolver(new SingleProfileConfigLoader(profile)), new ProfileServiceFactory(_hasher), console, console),
            console);
    }

    private long SeedSnapshot()
    {
        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath);
        var now = DateTimeOffset.UtcNow;
        var snapshot = repository.BeginSnapshot(now);
        var sourcePath = AbsolutePathMirrorMapper.ToMirrorPath(_sourceRoot);
        repository.RecordFileVersion(snapshot, Path.Combine(sourcePath, "nested", "added.txt"), null, "hash-a", 10, now, FileChangeKind.Added, now);
        repository.RecordFileVersion(snapshot, Path.Combine(sourcePath, "nested", "moved.txt"), @"old\moved.txt", "hash-m", 20, now, FileChangeKind.Moved, now);
        repository.RecordFileVersion(snapshot, Path.Combine(sourcePath, "nested", "deep", "depth-one.txt"), null, "hash-deep", 5, now, FileChangeKind.Added, now);
        repository.RecordFileVersion(snapshot, Path.Combine(sourcePath, "nested", "deep", "deeper", "depth-two.txt"), null, "hash-deeper", 5, now, FileChangeKind.Added, now);
        repository.RecordFileVersion(snapshot, Path.Combine(sourcePath, "deleted.txt"), null, null, 0, now, FileChangeKind.Deleted, now);
        repository.CompleteSnapshot(snapshot, now, new SnapshotStats(30, 1, 0, 1, 1, 0));
        return snapshot;
    }

    [Fact]
    public void Shows_snapshot_directory_changes_and_optional_file_details()
    {
        var snapshot = SeedSnapshot();
        var (command, console) = CreateCommand();

        var exitCode = command.Parse([snapshot.ToString(), "--profile", "test-profile", "--files"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.Contains($"Snapshot #{snapshot}", console.Output);
        Assert.Contains("[source]", console.Output);
        Assert.Contains("added.txt", console.Output);
        Assert.Contains("Moved from old\\moved.txt", console.Output);
        Assert.Contains("link 0", console.Output);
    }

    [Fact]
    public void Scoped_report_contains_only_changes_under_the_selected_directory()
    {
        var snapshot = SeedSnapshot();
        var (command, console) = CreateCommand();
        var sourcePath = AbsolutePathMirrorMapper.ToMirrorPath(_sourceRoot);
        var directory = Path.Combine(sourcePath, "nested");

        var exitCode = command.Parse([snapshot.ToString(), directory, "--profile", "test-profile", "--files"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.Contains("added.txt", console.Output);
        Assert.Contains("moved.txt", console.Output);
        Assert.DoesNotContain("deleted.txt", console.Output);
        Assert.Contains("+3 ~0 ->1 -0 link 0", console.Output);
    }

    [Fact]
    public void Reports_unknown_snapshot_without_creating_profile_storage()
    {
        var (command, console) = CreateCommand();

        var exitCode = command.Parse(["99", "--profile", "test-profile"]).Invoke();

        Assert.Equal(1, exitCode);
        Assert.Contains("snapshot #99 is not recorded", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
    }

    [Fact]
    public void Reports_empty_directory_scope_without_rendering_an_empty_tree()
    {
        var snapshot = SeedSnapshot();
        var (command, console) = CreateCommand();

        var exitCode = command.Parse([snapshot.ToString(), "unrelated", "--profile", "test-profile"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.Contains("No changes recorded", console.Output);
        Assert.DoesNotContain("Snapshot #", console.Output);
    }

    [Fact]
    public void Depth_option_limits_directories_and_file_details_without_changing_rollups()
    {
        var snapshot = SeedSnapshot();
        var (command, console) = CreateCommand();
        var sourcePath = AbsolutePathMirrorMapper.ToMirrorPath(_sourceRoot);
        var directory = Path.Combine(sourcePath, "nested");

        var exitCode = command.Parse([
            snapshot.ToString(), directory, "--profile", "test-profile", "--depth", "1", "--files",
        ]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.Contains("deep", console.Output);
        Assert.DoesNotContain("deeper (+", console.Output);
        Assert.Contains("... 1 deeper directory not shown", console.Output);
        Assert.Contains("added.txt", console.Output);
        Assert.Contains("depth-one.txt", console.Output);
        Assert.DoesNotContain("depth-two.txt", console.Output);
        Assert.Contains("+3", console.Output);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("invalid")]
    public void Rejects_invalid_depth_values_before_running_the_command(string depth)
    {
        var (command, _) = CreateCommand();

        var parseResult = command.Parse(["7", "--profile", "test-profile", "--depth", depth]);

        Assert.NotEmpty(parseResult.Errors);
        Assert.Contains("--depth must be a non-negative integer.", parseResult.Errors[0].Message);
    }

    [Fact]
    public void Requires_a_value_for_depth()
    {
        var (command, _) = CreateCommand();

        var parseResult = command.Parse(["7", "--depth"]);

        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void Help_documents_depth_as_optional_and_unlimited_by_default()
    {
        var (command, _) = CreateCommand();
        var depthOption = command.Options.Single(option => option.Name == "--depth");

        Assert.Contains("default: unlimited", depthOption.Description);
    }
}
