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

/// <summary>
/// Wraps <see cref="FakeHasher"/>, briefly holding each call open (via a short sleep)
/// so overlapping calls actually overlap in wall-clock time, and tracks the highest
/// number of concurrent <see cref="ComputeHash"/> calls observed. Used to prove a
/// configured <c>maxDegreeOfParallelism</c> - or its absence - actually bounds how
/// many operations run at once, rather than merely being passed to a constructor.
/// </summary>
internal sealed class ConcurrencyObservingHasher : IHasher
{
    private readonly FakeHasher _inner = new();
    private int _current;
    private int _maxObserved;

    public int MaxObservedConcurrency => _maxObserved;

    public string ComputeHash(Stream content)
    {
        var current = Interlocked.Increment(ref _current);
        InterlockedMax(ref _maxObserved, current);
        try
        {
            Thread.Sleep(20);
            return _inner.ComputeHash(content);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int initial;
        do
        {
            initial = target;
            if (candidate <= initial)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, candidate, initial) != initial);
    }
}

/// <summary>Fully in-memory content store standing in for <see cref="Vara.Infrastructure.Storage.FileSystemContentStore"/>. Thread-safe, since production execution parallelizes Add/Change operations.</summary>
internal sealed class FakeContentStore : IContentStore
{
    // Mirrors FileSystemContentStore's own chunked reads closely enough to exercise
    // "many small progress reports for one large file" in tests, without needing real
    // disk I/O or the production TeeStream plumbing.
    private const int SimulatedChunkSize = 8192;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _blobs = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Mirror = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeHasher _hasher = new();
    private readonly HashSet<string> _existingTargets = new(StringComparer.OrdinalIgnoreCase);
    private bool? _forcedSupportsHardlinks;

    /// <summary>Test seam: when set, <see cref="MoveMirrorEntry"/> throws this instead of performing the move.</summary>
    public Exception? ThrowOnMove { get; set; }

    /// <summary>Test seam: when set, <see cref="RemoveFromMirror"/> throws this instead of performing the removal.</summary>
    public Exception? ThrowOnRemove { get; set; }

    /// <summary>Test seam: destination paths for which <see cref="IsWithinMirror"/> should report containment.</summary>
    public HashSet<string> MirrorPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: marks <paramref name="absolutePath"/> as already existing, so <see cref="TargetExists"/> reports it.</summary>
    public void SeedExistingTarget(string absolutePath) => _existingTargets.Add(absolutePath);

    /// <summary>
    /// Test seam: simulates a target filesystem without hardlink support, so
    /// <see cref="PlaceAtMirrorPath"/> re-streams every placed blob's bytes (with
    /// progress) instead of being a free no-op - mirroring FileSystemContentStore's real
    /// fallback-copy behavior. Overrides whatever <see cref="ProbeHardlinkSupport"/>
    /// would otherwise report, including when called after it (e.g. by a pipeline run).
    /// </summary>
    public void ForceHardlinkSupportForTesting(bool supported) => SupportsHardlinks = (_forcedSupportsHardlinks = supported).Value;

    // Defaults to true (matching this fake's original always-true behavior) so tests that
    // exercise BackupExecutor directly - never calling ProbeHardlinkSupport, which only
    // BackupPipeline.Run does - don't accidentally take the fallback-copy simulation path.
    public bool SupportsHardlinks { get; private set; } = true;
    public bool ProbeHardlinkSupport() => SupportsHardlinks = _forcedSupportsHardlinks ?? true;

    public (string Hash, long Size) StoreFromStream(Stream content, Action<long>? onBytesWritten = null)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = _hasher.ComputeHash(new MemoryStream(bytes));
        _blobs.TryAdd(hash, bytes);
        ReportInChunks(bytes.LongLength, onBytesWritten);
        return (hash, bytes.LongLength);
    }

    public bool HasContent(string hash) => _blobs.ContainsKey(hash);

    public Stream OpenRead(string hash)
    {
        if (!_blobs.TryGetValue(hash, out var bytes))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.");
        }

        return new MemoryStream(bytes, writable: false);
    }

    public void PlaceAtMirrorPath(string hash, string mirrorRelativePath, Action<long>? onBytesCopied = null)
    {
        Mirror[mirrorRelativePath] = hash;
        if (!SupportsHardlinks && _blobs.TryGetValue(hash, out var bytes))
        {
            // Every placement falls back to a streamed copy on a target without
            // hardlink support, just like the real FileSystemContentStore.
            ReportInChunks(bytes.LongLength, onBytesCopied);
        }
    }

    private static void ReportInChunks(long totalBytes, Action<long>? onBytesReported)
    {
        if (onBytesReported is null)
        {
            return;
        }

        var remaining = totalBytes;
        while (remaining > 0)
        {
            var chunk = Math.Min(SimulatedChunkSize, remaining);
            onBytesReported(chunk);
            remaining -= chunk;
        }
    }

    public void MoveMirrorEntry(string fromRelativePath, string toRelativePath)
    {
        if (ThrowOnMove is not null)
        {
            throw ThrowOnMove;
        }

        if (Mirror.TryRemove(fromRelativePath, out var hash))
        {
            Mirror[toRelativePath] = hash;
            return;
        }

        if (Mirror.ContainsKey(toRelativePath))
        {
            // Mirrors FileSystemContentStore.MoveMirrorEntry's crash-recovery behavior:
            // the source is already gone but the destination already holds the expected
            // content, meaning a prior interrupted run already completed this relocation.
            return;
        }

        throw new FileNotFoundException($"No mirror entry at '{fromRelativePath}' or '{toRelativePath}'.");
    }

    public void RemoveFromMirror(string mirrorRelativePath)
    {
        if (ThrowOnRemove is not null)
        {
            throw ThrowOnRemove;
        }

        Mirror.TryRemove(mirrorRelativePath, out _);
    }

    public void DeleteContent(string hash) => _blobs.TryRemove(hash, out _);
    public void CleanupOrphanedTemp() { }
    public IReadOnlySet<string> ListAllStoredHashes() => _blobs.Keys.ToHashSet();

    public void ExtractTo(string hash, string destinationAbsolutePath, Action<long>? onBytesCopied = null)
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

        // Copies in fixed-size chunks (rather than one File.WriteAllBytes call) so tests
        // can exercise onBytesCopied's incremental-progress behavior against a fake store.
        const int chunkSize = 8192;
        using var destination = new FileStream(destinationAbsolutePath, FileMode.Create, FileAccess.Write);
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, bytes.Length - offset);
            destination.Write(bytes, offset, length);
            onBytesCopied?.Invoke(length);
        }
    }

    public bool IsWithinMirror(string absolutePath) => MirrorPaths.Contains(absolutePath);
    public bool TargetExists(string absolutePath) => _existingTargets.Contains(absolutePath);
}

/// <summary>In-memory manifest standing in for <see cref="Vara.Infrastructure.Snapshots.SqliteSnapshotRepository"/>.
/// Models <see cref="BeginManifestBatch"/> by buffering writes made during an open batch and
/// only folding them into the "durable" lists on <see cref="IManifestBatch.Commit"/> - this
/// approximates what would actually survive a real process crash before a commit, which is
/// exactly what the batch-manifest-writes change's tests need to assert.</summary>
internal sealed class FakeSnapshotRepository : ISnapshotRepository
{
    private readonly List<Snapshot> _snapshots = [];
    private readonly List<FileVersionRecord> _fileVersions = [];
    private long _nextSnapshotId = 1;
    private long _nextRecordId = 1;

    private List<FileVersionRecord>? _pendingFileVersions;
    private List<(long SnapshotId, DateTimeOffset At, SnapshotStatus Status, SnapshotStats Stats)>? _pendingOutcomes;
    private bool _batchActive;

    public int ReconcileCallCount { get; private set; }

    /// <summary>Number of times a manifest batch was durably committed - used by tests to
    /// assert that many manifest writes fold into one commit rather than one-per-file.</summary>
    public int CommitCount { get; private set; }

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
        long size, DateTimeOffset sourceModifiedAt, FileChangeKind changeKind, DateTimeOffset recordedAt,
        string? quickHash = null, int? quickHashScheme = null)
    {
        var record = new FileVersionRecord(
            _nextRecordId++, snapshotId, relativePath, previousRelativePath, contentHash, size, sourceModifiedAt, changeKind, recordedAt,
            quickHash, quickHashScheme);

        if (_batchActive)
        {
            (_pendingFileVersions ??= []).Add(record);
        }
        else
        {
            _fileVersions.Add(record);
        }
    }

    public void CompleteSnapshot(long snapshotId, DateTimeOffset completedAt, SnapshotStats stats) =>
        SetOutcome(snapshotId, completedAt, SnapshotStatus.Complete, stats);

    public void FailSnapshot(long snapshotId, DateTimeOffset failedAt, SnapshotStats stats) =>
        SetOutcome(snapshotId, failedAt, SnapshotStatus.Failed, stats);

    private void SetOutcome(long snapshotId, DateTimeOffset at, SnapshotStatus status, SnapshotStats stats)
    {
        if (_batchActive)
        {
            (_pendingOutcomes ??= []).Add((snapshotId, at, status, stats));
        }
        else
        {
            ApplyOutcome(snapshotId, at, status, stats);
        }
    }

    private void ApplyOutcome(long snapshotId, DateTimeOffset at, SnapshotStatus status, SnapshotStats stats)
    {
        var index = _snapshots.FindIndex(s => s.Id == snapshotId);
        _snapshots[index] = _snapshots[index] with { CompletedAt = at, Status = status, Stats = stats };
    }

    public IManifestBatch BeginManifestBatch()
    {
        if (_batchActive)
        {
            throw new InvalidOperationException("A manifest batch is already active on this repository.");
        }

        _batchActive = true;
        return new FakeManifestBatch(this);
    }

    private void CommitBatch()
    {
        if (_pendingFileVersions is not null)
        {
            _fileVersions.AddRange(_pendingFileVersions);
        }

        if (_pendingOutcomes is not null)
        {
            foreach (var (snapshotId, at, status, stats) in _pendingOutcomes)
            {
                ApplyOutcome(snapshotId, at, status, stats);
            }
        }

        CommitCount++;
        DiscardBatchState();
    }

    private void DiscardBatch() => DiscardBatchState();

    private void DiscardBatchState()
    {
        _pendingFileVersions = null;
        _pendingOutcomes = null;
        _batchActive = false;
    }

    private sealed class FakeManifestBatch(FakeSnapshotRepository owner) : IManifestBatch
    {
        private bool _finished;

        public void Commit()
        {
            if (_finished)
            {
                return;
            }

            owner.CommitBatch();
            _finished = true;
        }

        public void Dispose()
        {
            if (_finished)
            {
                return;
            }

            owner.DiscardBatch();
            _finished = true;
        }
    }

    public IReadOnlyDictionary<string, CurrentFileState> GetCurrentState()
    {
        var result = new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _fileVersions.GroupBy(r => r.RelativePath))
        {
            var latest = group.OrderByDescending(r => r.Id).First();
            if (latest.ChangeKind != FileChangeKind.Deleted)
            {
                result[latest.RelativePath] = new CurrentFileState(
                    latest.RelativePath, latest.ContentHash, latest.Size, latest.SourceModifiedAt, latest.QuickHash, latest.QuickHashScheme);
            }
        }

        return result;
    }

    public IReadOnlyDictionary<string, CurrentFileState> GetStateAsOf(DateTimeOffset asOf)
    {
        var result = new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _fileVersions.Where(r => r.RecordedAt <= asOf).GroupBy(r => r.RelativePath))
        {
            var latest = group.OrderByDescending(r => r.Id).First();
            if (latest.ChangeKind != FileChangeKind.Deleted)
            {
                result[latest.RelativePath] = new CurrentFileState(
                    latest.RelativePath, latest.ContentHash, latest.Size, latest.SourceModifiedAt, latest.QuickHash, latest.QuickHashScheme);
            }
        }

        return result;
    }

    public IReadOnlyList<FileVersionRecord> GetTombstones(DateTimeOffset? asOf)
    {
        var source = asOf is null ? _fileVersions.AsEnumerable() : _fileVersions.Where(r => r.RecordedAt <= asOf.Value);
        return source
            .GroupBy(r => r.RelativePath)
            .Select(g => g.OrderByDescending(r => r.Id).First())
            .Where(latest => latest.ChangeKind == FileChangeKind.Deleted)
            .ToList();
    }

    public IReadOnlyDictionary<string, string> GetMoveOrigins()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _fileVersions.GroupBy(r => r.RelativePath))
        {
            var latest = group.OrderByDescending(r => r.Id).First();
            if (latest is { ChangeKind: FileChangeKind.Moved, PreviousRelativePath: { } previousPath })
            {
                result[previousPath] = latest.RelativePath;
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
