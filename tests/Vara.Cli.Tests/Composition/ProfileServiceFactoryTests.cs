using Microsoft.Data.Sqlite;
using Vara.Application.History;
using Vara.Cli.Composition;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Xunit;

namespace Vara.Cli.Tests.Composition;

/// <summary>
/// Covers the fix-readonly-command-side-effects change's <c>createIfMissing: false</c> path
/// through the real composition point every read-only command (<c>snapshots</c>, <c>browse</c>,
/// etc.) uses, with real (not faked) <see cref="Vara.Infrastructure.Snapshots.SqliteSnapshotRepository"/>/
/// <see cref="Vara.Infrastructure.Storage.FileSystemContentStore"/> instances, since the guarantee
/// under test - no <c>.vara\</c> side effect - depends on their real filesystem/SQLite behavior.
/// </summary>
public class ProfileServiceFactoryTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}");
    private readonly ProfileServiceFactory _factory = new(new XxHash128Hasher());

    private Profile CreateFreshProfile() => new(
        "test-profile",
        _targetRoot,
        [new Source(Path.Combine(Path.GetTempPath(), $"vara-clitest-source-{Guid.NewGuid():N}"))],
        retention: null);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateFor_with_createIfMissing_false_does_not_create_vara_directory_for_a_fresh_profile()
    {
        var profile = CreateFreshProfile();

        using var services = _factory.CreateFor(profile, createIfMissing: false);

        Assert.False(Directory.Exists(_targetRoot));
        Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
    }

    [Fact]
    public void Snapshots_command_style_resolution_reports_no_snapshots_recorded_for_a_fresh_profile()
    {
        var profile = CreateFreshProfile();

        using var services = _factory.CreateFor(profile, createIfMissing: false);
        var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

        var snapshots = history.ListSnapshots();

        Assert.Empty(snapshots);
        Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
    }

    [Fact]
    public void Browse_command_style_resolution_reports_nothing_recorded_for_a_fresh_profile()
    {
        var profile = CreateFreshProfile();

        using var services = _factory.CreateFor(profile, createIfMissing: false);
        var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

        Assert.Throws<NoSuchDirectoryException>(() => history.ListDirectory(".", asOf: null, includeDeleted: false));
        Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
    }

    [Fact]
    public void CreateFor_default_still_eagerly_creates_vara_directory_for_a_fresh_profile()
    {
        var profile = CreateFreshProfile();

        using var services = _factory.CreateFor(profile);

        Assert.True(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
    }

    [Fact]
    public void Deleted_command_style_resolution_reports_no_deletions_for_a_fresh_profile()
    {
        var profile = CreateFreshProfile();

        using var services = _factory.CreateFor(profile, createIfMissing: false);
        var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

        var deleted = history.ListDeleted(directoryPath: null, since: null);

        Assert.Empty(deleted);
        Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
    }

    [Fact]
    public void History_command_style_resolution_reports_no_history_for_a_fresh_profile()
    {
        var profile = CreateFreshProfile();

        using var services = _factory.CreateFor(profile, createIfMissing: false);
        var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

        Assert.Throws<NoHistoryForPathException>(() => history.GetFileHistory("a.txt"));
        Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
    }

    [Fact]
    public void Show_command_style_resolution_reports_no_history_for_a_fresh_profile()
    {
        var profile = CreateFreshProfile();

        using var services = _factory.CreateFor(profile, createIfMissing: false);
        var history = new SnapshotHistoryService(services.Repository, services.ContentStore);
        using var destination = new MemoryStream();

        Assert.Throws<NoHistoryForPathException>(() => history.ShowVersion("a.txt", versionId: 1, asOf: null, destination));
        Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
    }

    [Fact]
    public void Diff_command_style_resolution_reports_no_history_for_a_fresh_profile()
    {
        var profile = CreateFreshProfile();

        using var services = _factory.CreateFor(profile, createIfMissing: false);
        var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

        Assert.Throws<NoHistoryForPathException>(() => history.OpenVersionsForDiff("a.txt", 1, null, 2, null));
        Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
    }

    [Fact]
    public void Restore_command_style_resolution_reports_no_history_for_a_fresh_profile()
    {
        var profile = CreateFreshProfile();
        var destination = Path.Combine(Path.GetTempPath(), $"vara-clitest-restore-{Guid.NewGuid():N}.txt");

        using var services = _factory.CreateFor(profile, createIfMissing: false);
        var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

        Assert.Throws<NoHistoryForPathException>(() => history.RestoreVersion("a.txt", 1, destination));
        Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
        Assert.False(File.Exists(destination));
    }
}
