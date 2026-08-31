using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Core.Tests.Abstractions;

/// <summary>
/// These tests exist to prove the Core ports are usable from a project that only
/// references Vara.Core (no Infrastructure/Application dependency), and that a
/// consumer can implement each port without any circular reference.
/// </summary>
public class PortAbstractionsTests
{
    [Fact]
    public void IProfileConfigLoader_can_be_implemented_and_used()
    {
        IProfileConfigLoader loader = new FakeProfileConfigLoader();

        var profiles = loader.LoadProfiles(loader.DefaultConfigPath);

        Assert.Single(profiles);
    }

    [Fact]
    public void ISnapshotRepository_can_be_implemented_and_used()
    {
        ISnapshotRepository repository = new FakeSnapshotRepository();

        var id = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.CompleteSnapshot(id, DateTimeOffset.UtcNow, SnapshotStats.Empty);

        Assert.NotEmpty(repository.ListSnapshots());
    }

    [Fact]
    public void IContentStore_can_be_implemented_and_used()
    {
        IContentStore store = new FakeContentStore();

        Assert.True(store.ProbeHardlinkSupport());
        Assert.True(store.SupportsHardlinks);
    }

    [Fact]
    public void IHasher_can_be_implemented_and_used()
    {
        IHasher hasher = new FakeHasher();

        using var stream = new MemoryStream([1, 2, 3]);
        Assert.Equal("fake-hash", hasher.ComputeHash(stream));
    }

    [Fact]
    public void IFileSystemScanner_can_be_implemented_and_used()
    {
        IFileSystemScanner scanner = new FakeFileSystemScanner();

        var result = scanner.Scan([new Source(@"C:\data")]);

        Assert.Empty(result.Entries);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void IRunLock_can_be_implemented_and_used()
    {
        using IRunLock runLock = new FakeRunLock();

        Assert.True(runLock.TryAcquire("files", @"D:\backup"));
    }

    private sealed class FakeProfileConfigLoader : IProfileConfigLoader
    {
        public string DefaultConfigPath => @"C:\fake\.vara\profiles.yml";

        public IReadOnlyList<Profile> LoadProfiles(string configPath) =>
            [new Profile("files", @"D:\backup", [new Source(@"C:\data")], null)];
    }

    private sealed class FakeSnapshotRepository : ISnapshotRepository
    {
        private readonly List<Snapshot> _snapshots = [];

        public void Dispose() { }
        public void ReconcileIncompleteSnapshots() { }

        public long BeginSnapshot(DateTimeOffset startedAt)
        {
            var id = _snapshots.Count + 1;
            _snapshots.Add(new Snapshot(id, startedAt, null, SnapshotStatus.Running, SnapshotStats.Empty));
            return id;
        }

        public void RecordFileVersion(long snapshotId, string relativePath, string? previousRelativePath, string contentHash, long size, DateTimeOffset sourceModifiedAt, FileChangeKind changeKind, DateTimeOffset recordedAt) { }

        public void CompleteSnapshot(long snapshotId, DateTimeOffset completedAt, SnapshotStats stats)
        {
            var index = _snapshots.FindIndex(s => s.Id == snapshotId);
            _snapshots[index] = _snapshots[index] with { CompletedAt = completedAt, Status = SnapshotStatus.Complete, Stats = stats };
        }

        public void FailSnapshot(long snapshotId, DateTimeOffset failedAt, SnapshotStats stats) { }
        public IManifestBatch BeginManifestBatch() => new NoOpManifestBatch();
        public IReadOnlyDictionary<string, CurrentFileState> GetCurrentState() => new Dictionary<string, CurrentFileState>();
        public IReadOnlyList<Snapshot> ListSnapshots() => _snapshots;
        public Snapshot? GetLastCompletedSnapshot() => _snapshots.LastOrDefault(s => s.Status == SnapshotStatus.Complete);
        public IReadOnlyList<FileVersionRecord> GetFileHistory(string relativePath) => [];
        public FileVersionRecord? FindVersionAsOf(string relativePath, DateTimeOffset asOf) => null;
        public void DeleteSnapshot(long snapshotId) { }
        public int PruneSnapshots(IReadOnlyList<long> snapshotIds) => 0;
        public IReadOnlySet<string> GetAllReferencedContentHashes() => new HashSet<string>();

        private sealed class NoOpManifestBatch : IManifestBatch
        {
            public void Commit() { }
            public void Dispose() { }
        }
    }

    private sealed class FakeContentStore : IContentStore
    {
        public bool ProbeHardlinkSupport() => SupportsHardlinks = true;
        public bool SupportsHardlinks { get; private set; }
        public (string Hash, long Size) StoreFromStream(Stream content) => ("fake-hash", content.Length);
        public bool HasContent(string hash) => false;
        public void PlaceAtMirrorPath(string hash, string mirrorRelativePath) { }
        public void MoveMirrorEntry(string fromRelativePath, string toRelativePath) { }
        public void RemoveFromMirror(string mirrorRelativePath) { }
        public void DeleteContent(string hash) { }
        public void CleanupOrphanedTemp() { }
        public IReadOnlySet<string> ListAllStoredHashes() => new HashSet<string>();
        public void ExtractTo(string hash, string destinationAbsolutePath) { }
        public bool IsWithinMirror(string absolutePath) => false;
        public bool TargetExists(string absolutePath) => false;
    }

    private sealed class FakeHasher : IHasher
    {
        public string ComputeHash(Stream content) => "fake-hash";
    }

    private sealed class FakeFileSystemScanner : IFileSystemScanner
    {
        public ScanResult Scan(IReadOnlyList<Source> sources) => new([], []);
    }

    private sealed class FakeRunLock : IRunLock
    {
        public void Dispose() { }
        public bool TryAcquire(string profileName, string targetRoot) => true;
    }
}
