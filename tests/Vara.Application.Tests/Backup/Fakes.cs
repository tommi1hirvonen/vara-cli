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

    /// <summary>Test seam: when set, <see cref="PlaceAtMirrorPath"/> throws this instead of performing the placement - only for a call whose <c>mirrorRelativePath</c> equals <see cref="ThrowOnPlaceForPath"/> (or for every call, when that is left unset), so a test can fail one specific placement while others in the same run proceed normally.</summary>
    public Exception? ThrowOnPlace { get; set; }

    /// <summary>Test seam: restricts <see cref="ThrowOnPlace"/> to only the placement whose <c>mirrorRelativePath</c> matches this value. Unset (<see langword="null"/>) means every placement throws.</summary>
    public string? ThrowOnPlaceForPath { get; set; }

    /// <summary>Test seam: when set, <see cref="MoveMirrorEntry"/> throws this instead of performing the move.</summary>
    public Exception? ThrowOnMove { get; set; }

    /// <summary>Test seam: when set, <see cref="RemoveFromMirror"/> throws this instead of performing the removal.</summary>
    public Exception? ThrowOnRemove { get; set; }

    /// <summary>Test seam: destination paths for which <see cref="IsWithinMirror"/> should report containment.</summary>
    public HashSet<string> MirrorPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: records the <c>previousContentHash</c> argument passed to each <see cref="PlaceAtMirrorPath"/> call, keyed by mirror path - lets a test assert BackupExecutor threads the previous content's hash through correctly for a Changed operation.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string?> PreviousContentHashesSeen { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: records the <c>hash</c> argument passed to each <see cref="RemoveFromMirror"/> call, keyed by mirror path - lets a test assert BackupExecutor threads the deleted file's known hash through correctly.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> RemoveFromMirrorHashesSeen { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Test seam: when set, <see cref="OpenRead"/> throws this instead of returning a stream for a call whose <c>hash</c> equals this value - lets a test simulate one side of a diff failing to open after the other side already opened successfully, to verify the successfully-opened stream is disposed rather than leaked.</summary>
    public string? ThrowOnOpenReadForHash { get; set; }

    /// <summary>Test seam: records each hash whose returned stream has been disposed, so a test can assert a stream opened for one side of a diff was released even though the caller never got to read from it.</summary>
    public System.Collections.Concurrent.ConcurrentBag<string> DisposedHashes { get; } = new();

    public Stream OpenRead(string hash)
    {
        if (ThrowOnOpenReadForHash is not null && string.Equals(ThrowOnOpenReadForHash, hash, StringComparison.Ordinal))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.");
        }

        if (!_blobs.TryGetValue(hash, out var bytes))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.");
        }

        return new DisposeTrackingStream(bytes, hash, DisposedHashes);
    }

    /// <summary>Wraps a read-only <see cref="MemoryStream"/> so <see cref="FakeContentStore.OpenRead"/> can report when its caller disposes it, without changing any other observable behavior.</summary>
    private sealed class DisposeTrackingStream(byte[] bytes, string hash, System.Collections.Concurrent.ConcurrentBag<string> disposedHashes) : MemoryStream(bytes, writable: false)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposedHashes.Add(hash);
            }

            base.Dispose(disposing);
        }
    }

    public void PlaceAtMirrorPath(string hash, string mirrorRelativePath, Action<long>? onBytesCopied = null, string? previousContentHash = null)
    {
        if (ThrowOnPlace is not null && (ThrowOnPlaceForPath is null || string.Equals(ThrowOnPlaceForPath, mirrorRelativePath, StringComparison.OrdinalIgnoreCase)))
        {
            throw ThrowOnPlace;
        }

        Mirror[mirrorRelativePath] = hash;
        PreviousContentHashesSeen[mirrorRelativePath] = previousContentHash;
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

    public void RemoveFromMirror(string mirrorRelativePath, string hash)
    {
        if (ThrowOnRemove is not null)
        {
            throw ThrowOnRemove;
        }

        RemoveFromMirrorHashesSeen[mirrorRelativePath] = hash;
        Mirror.TryRemove(mirrorRelativePath, out _);
    }

    public void DeleteContent(string hash) => _blobs.TryRemove(hash, out _);
    public void CleanupOrphanedTemp() { }
    public IReadOnlySet<string> ListAllStoredHashes() => _blobs.Keys.ToHashSet();

    /// <summary>Test seam: overwrites an already-stored blob's bytes with different
    /// content, without changing the hash key it's filed under - simulates target-side
    /// bit rot/corruption for integrity-check tests, where a blob's on-disk bytes no
    /// longer match the hash the manifest recorded for it.</summary>
    public void CorruptBlob(string hash, string corruptedContent) => _blobs[hash] = System.Text.Encoding.UTF8.GetBytes(corruptedContent);

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

    public void RemoveExtractedFile(string absolutePath)
    {
        _existingTargets.Remove(absolutePath);
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
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

    /// <summary>When set, the next <see cref="IManifestBatch.Commit"/> call throws this
    /// exception instead of committing - used to simulate a secondary failure while
    /// <see cref="BackupPipeline"/>'s catch block is itself trying to record a run's
    /// failure state.</summary>
    public Exception? ThrowOnNextCommit { get; set; }

    /// <summary>When set, invoked once at the end of every <see cref="RecordFileVersion"/>
    /// call - used by cancellation tests to deterministically trigger cancellation
    /// partway through a sequential (Move/Delete/Link) run.</summary>
    public Action? OnRecordFileVersion { get; set; }

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
        long snapshotId, string relativePath, string? previousRelativePath, string? contentHash,
        long size, DateTimeOffset sourceModifiedAt, FileChangeKind changeKind, DateTimeOffset recordedAt,
        string? quickHash = null, int? quickHashScheme = null, string? linkTarget = null)
    {
        var record = new FileVersionRecord(
            _nextRecordId++, snapshotId, relativePath, previousRelativePath, contentHash, size, sourceModifiedAt, changeKind, recordedAt,
            quickHash, quickHashScheme, linkTarget);

        if (_batchActive)
        {
            (_pendingFileVersions ??= []).Add(record);
        }
        else
        {
            _fileVersions.Add(record);
        }

        OnRecordFileVersion?.Invoke();
    }

    public void CompleteSnapshot(long snapshotId, DateTimeOffset completedAt, SnapshotStats stats) =>
        SetOutcome(snapshotId, completedAt, SnapshotStatus.Complete, stats);

    public void FailSnapshot(long snapshotId, DateTimeOffset failedAt, SnapshotStats stats) =>
        SetOutcome(snapshotId, failedAt, SnapshotStatus.Failed, stats);

    public void CancelSnapshot(long snapshotId, DateTimeOffset cancelledAt, SnapshotStats stats) =>
        SetOutcome(snapshotId, cancelledAt, SnapshotStatus.Cancelled, stats);

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

        // Reopen: clear the pending buffers but keep the batch active, mirroring the
        // real repository's "commit and immediately begin a new transaction" checkpoint
        // behavior - a commit is a checkpoint, not necessarily the batch's final one.
        _pendingFileVersions = null;
        _pendingOutcomes = null;
    }

    private void DiscardBatch()
    {
        _pendingFileVersions = null;
        _pendingOutcomes = null;
        _batchActive = false;
    }

    private sealed class FakeManifestBatch(FakeSnapshotRepository owner) : IManifestBatch
    {
        private bool _disposed;

        public void Commit()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (owner.ThrowOnNextCommit is { } exception)
            {
                owner.ThrowOnNextCommit = null;
                throw exception;
            }

            owner.CommitBatch();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            owner.DiscardBatch();
            _disposed = true;
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
                    latest.RelativePath, latest.ContentHash, latest.Size, latest.SourceModifiedAt, latest.QuickHash, latest.QuickHashScheme,
                    latest.ChangeKind == FileChangeKind.Linked, latest.LinkTarget);
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
                    latest.RelativePath, latest.ContentHash, latest.Size, latest.SourceModifiedAt, latest.QuickHash, latest.QuickHashScheme,
                    latest.ChangeKind == FileChangeKind.Linked, latest.LinkTarget);
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
    public IReadOnlyList<FileVersionRecord> GetFileHistoryUnderPrefix(string prefix) =>
        _fileVersions.Where(r => prefix.Length == 0 || r.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Id).ToList();
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

    public IReadOnlySet<string> GetAllReferencedContentHashes() =>
        _fileVersions.Where(r => r.ContentHash is not null).Select(r => r.ContentHash!).ToHashSet();
    public IReadOnlyList<string> GetPathsForContentHash(string hash) => _fileVersions.Where(r => r.ContentHash == hash).Select(r => r.RelativePath).Distinct().ToList();
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
