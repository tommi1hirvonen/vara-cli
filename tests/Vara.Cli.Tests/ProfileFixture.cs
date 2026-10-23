using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;
using Profile = Vara.Core.Configuration.Profile;

namespace Vara.Cli.Tests;

/// <summary>
/// A throwaway profile (target root + source root under the temp directory) backed by the
/// real repository and content store, with helpers for recording file versions, so command
/// tests exercise the real wiring instead of fakes.
/// </summary>
internal sealed class ProfileFixture : IDisposable
{
    private readonly XxHash128Hasher _hasher = new();

    public ProfileFixture(RetentionPolicy? retention = null)
    {
        Profile = new Profile("test-profile", TargetRoot, [new Source(SourceRoot)], retention);
    }

    public string TargetRoot { get; } = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}");

    public string SourceRoot { get; } = Path.Combine(Path.GetTempPath(), $"vara-clitest-src-{Guid.NewGuid():N}");

    public Profile Profile { get; }

    public ProfileResolver Resolver => new(new SingleProfileConfigLoader(Profile));

    public ProfileServiceFactory Services => new(_hasher);

    /// <summary>Records one snapshot containing a single version of <paramref name="path"/>.</summary>
    /// <returns>The recorded version's id.</returns>
    public long Record(string path, string content, FileChangeKind kind, DateTimeOffset at)
    {
        var (hash, size) = new FileSystemContentStore(TargetRoot, _hasher)
            .StoreFromStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));

        using var repository = new SqliteSnapshotRepository(Path.Combine(TargetRoot, ".vara", "profile.db"));
        var snapshot = repository.BeginSnapshot(at);
        repository.RecordFileVersion(snapshot, path, null, hash, size, at, kind, at);
        repository.CompleteSnapshot(snapshot, at, SnapshotStats.Empty);
        return repository.GetFileHistory(path).Max(v => v.Id);
    }

    /// <summary>The ids of every snapshot currently recorded for the profile.</summary>
    public IReadOnlyList<long> SnapshotIds()
    {
        using var repository = new SqliteSnapshotRepository(
            Path.Combine(TargetRoot, ".vara", "profile.db"), createIfMissing: false);
        return repository.ListSnapshots().Select(s => s.Id).ToList();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(TargetRoot))
        {
            Directory.Delete(TargetRoot, recursive: true);
        }
    }
}
