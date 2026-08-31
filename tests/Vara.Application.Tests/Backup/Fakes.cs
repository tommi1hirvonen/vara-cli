using System.Security.Cryptography;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;

namespace Vara.Application.Tests.Backup;

/// <summary>Deterministic content-based fake hasher (SHA-256, hex) - no production dependency, just distinguishes content for tests.</summary>
internal sealed class FakeHasher : IHasher
{
    public string ComputeHash(Stream content)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}

/// <summary>Fully in-memory content store standing in for <see cref="Vara.Infrastructure.Storage.FileSystemContentStore"/>. Thread-safe, since production execution parallelizes Add/Change operations.</summary>
internal sealed class FakeContentStore : IContentStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _blobs = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Mirror = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeHasher _hasher = new();

    public bool SupportsHardlinks { get; private set; }
    public bool ProbeHardlinkSupport() => SupportsHardlinks = true;

    public (string Hash, long Size) StoreFromStream(Stream content)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = _hasher.ComputeHash(new MemoryStream(bytes));
        _blobs.TryAdd(hash, bytes);
        return (hash, bytes.LongLength);
    }

    public bool HasContent(string hash) => _blobs.ContainsKey(hash);
    public void PlaceAtMirrorPath(string hash, string mirrorRelativePath) => Mirror[mirrorRelativePath] = hash;

    public void MoveMirrorEntry(string fromRelativePath, string toRelativePath)
    {
        if (Mirror.TryRemove(fromRelativePath, out var hash))
        {
            Mirror[toRelativePath] = hash;
        }
    }

    public void RemoveFromMirror(string mirrorRelativePath) => Mirror.TryRemove(mirrorRelativePath, out _);
    public void DeleteContent(string hash) => _blobs.TryRemove(hash, out _);
    public void CleanupOrphanedTemp() { }
    public IReadOnlySet<string> ListAllStoredHashes() => _blobs.Keys.ToHashSet();

    public void ExtractTo(string hash, string destinationAbsolutePath)
    {
        if (!_blobs.TryGetValue(hash, out var bytes))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.");
        }

        var directory = Path.GetDirectoryName(destinationAbsolutePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(destinationAbsolutePath, bytes);
    }
}

/// <summary>In-memory manifest standing in for <see cref="Vara.Infrastructure.Snapshots.SqliteSnapshotRepository"/>.</summary>
internal sealed class FakeSnapshotRepository : ISnapshotRepository
{
    private readonly List<Snapshot> _snapshots = [];
    private readonly List<FileVersionRecord> _fileVersions = [];
    private long _nextSnapshotId = 1;
    private long _nextRecordId = 1;

    public int ReconcileCallCount { get; private set; }

    public void Dispose() { }

    public void ReconcileIncompleteSnapshots()
    {
        ReconcileCallCount++;
        for (var i = 0; i < _snapshots.Count; i++)
        {
            if (_snapshots[i].Status == SnapshotStatus.Running)
            {
                _snapshots[i] = _snapshots[i] with { Status = SnapshotStatus.Failed };
            }
        }
    }

    public long BeginSnapshot(DateTimeOffset startedAt)
    {
        var id = _nextSnapshotId++;
        _snapshots.Add(new Snapshot(id, startedAt, null, SnapshotStatus.Running, SnapshotStats.Empty));
        return id;
    }

    public void RecordFileVersion(
        long snapshotId, string relativePath, string? previousRelativePath, string contentHash,
        long size, DateTimeOffset sourceModifiedAt, FileChangeKind changeKind, DateTimeOffset recordedAt) =>
        _fileVersions.Add(new FileVersionRecord(
            _nextRecordId++, snapshotId, relativePath, previousRelativePath, contentHash, size, sourceModifiedAt, changeKind, recordedAt));

    public void CompleteSnapshot(long snapshotId, DateTimeOffset completedAt, SnapshotStats stats) =>
        SetOutcome(snapshotId, completedAt, SnapshotStatus.Complete, stats);

    public void FailSnapshot(long snapshotId, DateTimeOffset failedAt, SnapshotStats stats) =>
        SetOutcome(snapshotId, failedAt, SnapshotStatus.Failed, stats);

    private void SetOutcome(long snapshotId, DateTimeOffset at, SnapshotStatus status, SnapshotStats stats)
    {
        var index = _snapshots.FindIndex(s => s.Id == snapshotId);
        _snapshots[index] = _snapshots[index] with { CompletedAt = at, Status = status, Stats = stats };
    }

    public IReadOnlyDictionary<string, CurrentFileState> GetCurrentState()
    {
        var result = new Dictionary<string, CurrentFileState>();
        foreach (var group in _fileVersions.GroupBy(r => r.RelativePath))
        {
            var latest = group.OrderByDescending(r => r.Id).First();
            if (latest.ChangeKind != FileChangeKind.Deleted)
            {
                result[latest.RelativePath] = new CurrentFileState(latest.RelativePath, latest.ContentHash, latest.Size, latest.SourceModifiedAt);
            }
        }

        return result;
    }

    public IReadOnlyList<Snapshot> ListSnapshots() => _snapshots.OrderByDescending(s => s.Id).ToList();
    public Snapshot? GetLastCompletedSnapshot() => _snapshots.Where(s => s.Status == SnapshotStatus.Complete).OrderByDescending(s => s.Id).FirstOrDefault();
    public IReadOnlyList<FileVersionRecord> GetFileHistory(string relativePath) => _fileVersions.Where(r => r.RelativePath == relativePath).OrderByDescending(r => r.Id).ToList();
    public FileVersionRecord? FindVersionAsOf(string relativePath, DateTimeOffset asOf) =>
        GetFileHistory(relativePath).FirstOrDefault(r => r.RecordedAt <= asOf) is { ChangeKind: not FileChangeKind.Deleted } match ? match : null;

    public void DeleteSnapshot(long snapshotId)
    {
        _snapshots.RemoveAll(s => s.Id == snapshotId);
        _fileVersions.RemoveAll(r => r.SnapshotId == snapshotId);
    }

    public int PruneSnapshots(IReadOnlyList<long> snapshotIds)
    {
        if (snapshotIds.Count == 0)
        {
            return 0;
        }

        // The current (latest, non-deleted) row per path is protected, mirroring the
        // real repository's rule: never silently orphan a still-live path. The "current"
        // row must be the true latest row for its path across ALL rows (including
        // deletion tombstones) - only protected if that latest row isn't itself a deletion.
        var protectedIds = _fileVersions
            .GroupBy(r => r.RelativePath)
            .Select(g => g.OrderByDescending(r => r.Id).First())
            .Where(latest => latest.ChangeKind != FileChangeKind.Deleted)
            .Select(latest => latest.Id)
            .ToHashSet();

        var idSet = snapshotIds.ToHashSet();
        _fileVersions.RemoveAll(r => idSet.Contains(r.SnapshotId) && !protectedIds.Contains(r.Id));

        var stillReferenced = _fileVersions.Select(r => r.SnapshotId).ToHashSet();
        var removable = idSet.Where(id => !stillReferenced.Contains(id)).ToList();
        _snapshots.RemoveAll(s => removable.Contains(s.Id));

        return removable.Count;
    }

    public IReadOnlySet<string> GetAllReferencedContentHashes() => _fileVersions.Select(r => r.ContentHash).ToHashSet();
}

/// <summary>Fake scanner returning a pre-set, test-controlled list of entries (and, optionally, scan failures) regardless of the sources argument.</summary>
internal sealed class FakeFileSystemScanner(IReadOnlyList<ScannedEntry> entries, IReadOnlyList<ScanFailure>? failures = null) : IFileSystemScanner
{
    public ScanResult Scan(IReadOnlyList<Source> sources) => new(entries, failures ?? []);
}

/// <summary>Fake run lock whose acquisition outcome is test-controlled.</summary>
internal sealed class FakeRunLock(bool acquirable = true) : IRunLock
{
    public bool AcquireAttempted { get; private set; }
    public void Dispose() { }
    public bool TryAcquire(string profileName, string targetRoot)
    {
        AcquireAttempted = true;
        return acquirable;
    }
}
