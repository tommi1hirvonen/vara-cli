using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Infrastructure.Concurrency;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;

namespace Vara.Cli.Composition;

/// <summary>
/// Constructs the profile-scoped Infrastructure adapters (manifest, content store, run
/// lock) for a resolved <see cref="Profile"/>. These cannot be registered as ordinary
/// singleton DI services because which profile is in play is only known once the CLI
/// arguments have been parsed - this factory is the composition root's answer to that.
/// </summary>
public sealed class ProfileServiceFactory(IHasher hasher)
{
    /// <param name="createIfMissing">
    /// When <see langword="true"/> (the default), eagerly creates the profile's
    /// <c>.vara\</c> backing storage (manifest database, versions/temp directories) if it
    /// doesn't already exist - the behavior every call site had before the
    /// fix-readonly-command-side-effects change. Pass <see langword="false"/> for
    /// read-only commands so that resolving services for a profile that has never
    /// completed a backup run does not itself create that storage as a side effect.
    /// </param>
    public ProfileServices CreateFor(Profile profile, bool createIfMissing = true)
    {
        var dbPath = Path.Combine(profile.TargetRoot, ".vara", "profile.db");
        var repository = new SqliteSnapshotRepository(dbPath, createIfMissing);
        var contentStore = new FileSystemContentStore(profile.TargetRoot, hasher, createIfMissing);
        var runLock = new FileRunLock();

        return new ProfileServices(repository, contentStore, runLock);
    }
}

/// <summary>The set of profile-scoped services needed to operate on one profile.</summary>
public sealed class ProfileServices(ISnapshotRepository repository, IContentStore contentStore, IRunLock runLock) : IDisposable
{
    public ISnapshotRepository Repository { get; } = repository;
    public IContentStore ContentStore { get; } = contentStore;
    public IRunLock RunLock { get; } = runLock;

    public void Dispose()
    {
        Repository.Dispose();
        // RunLock is disposed by whichever pipeline/service acquires it (BackupPipeline,
        // PruneService), since it is only actually held for the duration of that
        // specific operation.
    }
}
