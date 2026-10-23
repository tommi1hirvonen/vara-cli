using Vara.Core.Abstractions;
using Vara.Core.Snapshots;

namespace Vara.Application.Tests.Backup;

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
            if (latest is { ChangeKind: FileChangeKind.MovedChange, PreviousRelativePath: { } previousPath })
            {
                result[previousPath] = latest.RelativePath;
            }
        }

        return result;
    }

    public IReadOnlyList<Snapshot> ListSnapshots() => _snapshots.OrderByDescending(s => s.Id).ToList();
    public Snapshot? GetLastCompletedSnapshot() =>
        _snapshots.Where(s => s.Status == SnapshotStatus.Complete).OrderByDescending(s => s.Id).FirstOrDefault();
    public IReadOnlyList<FileVersionRecord> GetFileHistory(string relativePath) =>
        _fileVersions.Where(r => r.RelativePath == relativePath).OrderByDescending(r => r.Id).ToList();
    public IReadOnlyList<FileVersionRecord> GetFileHistoryUnderPrefix(string prefix) =>
        _fileVersions
            .Where(r => prefix.Length == 0 || r.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Id)
            .ToList();
    public IReadOnlyList<FileVersionRecord> GetSnapshotFileChanges(long snapshotId, string prefix) =>
        _fileVersions
            .Where(r => r.SnapshotId == snapshotId &&
                (prefix.Length == 0 || r.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Id)
            .ToList();
    public FileVersionRecord? FindVersionAsOf(string relativePath, DateTimeOffset asOf) =>
        GetFileHistory(relativePath).FirstOrDefault(r => r.RecordedAt <= asOf) is
            { ChangeKind: not FileChangeKind.DeletedChange } match ? match : null;

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
    public IReadOnlyDictionary<string, long> GetAllReferencedContentSizes() =>
        _fileVersions
            .Where(r => r.ContentHash is not null)
            .GroupBy(r => r.ContentHash!)
            .ToDictionary(g => g.Key, g => g.Max(r => r.Size));
    public IReadOnlyList<string> GetPathsForContentHash(string hash) =>
        _fileVersions.Where(r => r.ContentHash == hash).Select(r => r.RelativePath).Distinct().ToList();
}
