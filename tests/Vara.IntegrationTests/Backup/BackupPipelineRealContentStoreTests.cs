using Microsoft.Data.Sqlite;
using Vara.Application.Backup;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;
using Vara.IntegrationTests.TestSupport;
using Xunit;

namespace Vara.IntegrationTests.Backup;

/// <summary>
/// Runs <see cref="BackupPipeline"/> against the real <see cref="FileSystemContentStore"/>
/// (not the in-memory <c>FakeContentStore</c>) specifically to exercise its real streamed
/// copy+hash path end-to-end - see stream-large-file-transfer-progress change's design.md.
/// <c>FakeContentStore</c>'s simulated chunking (Vara.Application.Tests/Backup/Fakes.cs) is a
/// reasonable stand-in for most tests, but this one needs the production
/// TeeStream/BufferedStream plumbing itself, not a simulation of it.
/// </summary>
public class BackupPipelineRealContentStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vara-inttest-{Guid.NewGuid():N}.db");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-inttest-src-{Guid.NewGuid():N}");
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-inttest-tgt-{Guid.NewGuid():N}");
    private SqliteSnapshotRepository? _repository;
    private FileSystemContentStore? _contentStore;

    private SqliteSnapshotRepository Repository => _repository ??= new SqliteSnapshotRepository(_dbPath);
    private FileSystemContentStore ContentStore => _contentStore ??= new FileSystemContentStore(_targetRoot, new XxHash128Hasher());

    public BackupPipelineRealContentStoreTests() => Directory.CreateDirectory(_sourceRoot);

    public void Dispose()
    {
        _repository?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        if (Directory.Exists(_sourceRoot))
        {
            Directory.Delete(_sourceRoot, recursive: true);
        }

        DirectoryCleanup.ClearReadOnlyAndDelete(_targetRoot);
    }

    private static Profile SimpleProfile(string sourceRoot, string targetRoot) =>
        new("files", targetRoot, [new Source(sourceRoot)], null, null);

    [Fact]
    public void Backing_up_a_single_large_file_reports_progress_incrementally_through_the_real_content_store()
    {
        const int contentLength = 5 * 1024 * 1024;
        var bytes = new byte[contentLength];
        Random.Shared.NextBytes(bytes);
        var path = Path.Combine(_sourceRoot, "large.bin");
        File.WriteAllBytes(path, bytes);
        var entry = new ScannedEntry("large.bin", path, contentLength, File.GetLastWriteTimeUtc(path), false, null);

        var pipeline = new BackupPipeline(
            new FakeFileSystemScanner([entry]), new XxHash128Hasher(), ContentStore, Repository, new FakeRunLock());
        var progress = new SyncProgress<BackupProgress>();

        var result = pipeline.Run(SimpleProfile(_sourceRoot, _targetRoot), progress);

        Assert.Equal(1, result.Stats.FilesAdded);
        Assert.Equal(contentLength, result.Stats.BytesTransferred);

        var byTotal = progress.Reports.OrderBy(r => r.BytesTransferred).ToList();
        Assert.True(byTotal.Count > 2, $"expected more than one intermediate progress report, observed {byTotal.Count}");
        Assert.Equal(0, byTotal[0].BytesTransferred);
        Assert.Equal(contentLength, byTotal[^1].BytesTransferred);

        // Every report's cumulative total is non-decreasing (per the progress-reporting spec's
        // "never regresses" requirement), and there's at least one strictly-between-0-and-full
        // report - proof the display isn't frozen for the whole transfer, per the
        // "Incremental progress during in-flight file transfers" requirement.
        for (var i = 1; i < byTotal.Count; i++)
        {
            Assert.True(byTotal[i].BytesTransferred >= byTotal[i - 1].BytesTransferred);
        }

        Assert.Contains(byTotal, r => r.BytesTransferred > 0 && r.BytesTransferred < contentLength);
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> - invokes the callback on the reporting
    /// thread directly instead of <see cref="Progress{T}"/>'s SynchronizationContext-posted,
    /// deferred delivery, so assertions after Run returns see every report.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly object _sync = new();
        public List<T> Reports { get; } = [];

        public void Report(T value)
        {
            lock (_sync)
            {
                Reports.Add(value);
            }
        }
    }
}
