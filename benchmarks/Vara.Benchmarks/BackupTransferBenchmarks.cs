using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Vara.Application.Backup;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Infrastructure.Concurrency;
using Vara.Infrastructure.FileSystem;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;

namespace Vara.Benchmarks;

public abstract class BackupBenchmarkBase
{
    protected string ScratchDirectory = string.Empty;
    protected string SourceRoot = string.Empty;
    protected string TargetRoot = string.Empty;
    protected string DatabasePath = string.Empty;
    protected readonly XxHash128Hasher Hasher = new();

    [Params(8, 64)]
    public int FileCount { get; set; }

    [Params(65_536, 1_048_576)]
    public int BytesPerFile { get; set; }

    protected void CreateSourceFixture()
    {
        ScratchDirectory = BenchmarkFiles.CreateScratchDirectory();
        SourceRoot = Path.Combine(ScratchDirectory, "source");
        TargetRoot = Path.Combine(ScratchDirectory, "target");
        Directory.CreateDirectory(SourceRoot);

        for (var i = 0; i < FileCount; i++)
        {
            File.WriteAllBytes(
                Path.Combine(SourceRoot, $"file-{i:D4}.bin"),
                BenchmarkFiles.CreateDeterministicBytes(BytesPerFile, i));
        }
    }

    protected void ResetTarget()
    {
        SqliteConnection.ClearAllPools();
        BenchmarkFiles.DeleteDirectory(TargetRoot);
        Directory.CreateDirectory(TargetRoot);
        DatabasePath = Path.Combine(TargetRoot, ".vara", "profile.db");
    }

    protected void CleanupWorkspace()
    {
        SqliteConnection.ClearAllPools();
        BenchmarkFiles.DeleteDirectory(ScratchDirectory);
    }
}

[MemoryDiagnoser]
public class BackupExecutorBenchmarks : BackupBenchmarkBase
{
    private FileSystemContentStore _contentStore = null!;
    private SqliteSnapshotRepository _repository = null!;
    private IManifestBatch _manifestBatch = null!;
    private BackupExecutor _executor = null!;
    private BackupPlan _plan = null!;
    private long _snapshotId;
    private DateTimeOffset _recordedAt;

    [GlobalSetup]
    public void Setup() => CreateSourceFixture();

    [IterationSetup]
    public void SetupIteration()
    {
        DisposeCurrentIteration();
        ResetTarget();
        _contentStore = new FileSystemContentStore(TargetRoot, Hasher);
        _contentStore.ProbeHardlinkSupport();
        _repository = new SqliteSnapshotRepository(DatabasePath);
        _recordedAt = DateTimeOffset.UtcNow;
        _snapshotId = _repository.BeginSnapshot(_recordedAt);
        _manifestBatch = _repository.BeginManifestBatch();
        _executor = new BackupExecutor(_contentStore, _repository, Hasher, maxDegreeOfParallelism: 1);

        var operations = new List<PlannedOperation>(FileCount);
        long totalBytes = 0;
        for (var i = 0; i < FileCount; i++)
        {
            var path = Path.Combine(SourceRoot, $"file-{i:D4}.bin");
            var file = new FileInfo(path);
            operations.Add(new AddOperation(
                $"source\\file-{i:D4}.bin",
                path,
                file.Length,
                file.LastWriteTimeUtc));
            totalBytes += file.Length;
        }

        _plan = new BackupPlan(operations, totalBytes);
    }

    [IterationCleanup]
    public void CleanupIteration()
    {
        if (_manifestBatch is not null)
        {
            _manifestBatch.Commit();
            _manifestBatch.Dispose();
            _manifestBatch = null!;
        }

        _repository?.Dispose();
        _repository = null!;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        DisposeCurrentIteration();
        CleanupWorkspace();
    }

    [Benchmark]
    public ExecutionOutcome ExecuteTransfers() =>
        _executor.Execute(_snapshotId, _recordedAt, _plan, manifestBatch: _manifestBatch);

    private void DisposeCurrentIteration()
    {
        _manifestBatch?.Dispose();
        _manifestBatch = null!;
        _repository?.Dispose();
        _repository = null!;
    }
}

[MemoryDiagnoser]
public class BackupPipelineBenchmarks : BackupBenchmarkBase
{
    private SqliteSnapshotRepository _repository = null!;
    private BackupPipeline _pipeline = null!;
    private Profile _profile = null!;

    [GlobalSetup]
    public void Setup()
    {
        CreateSourceFixture();
        _profile = new Profile("benchmark", TargetRoot, [new Source(SourceRoot)], retention: null);
    }

    [IterationSetup]
    public void SetupIteration()
    {
        _repository?.Dispose();
        _repository = null!;
        ResetTarget();
        _repository = new SqliteSnapshotRepository(DatabasePath);
        var contentStore = new FileSystemContentStore(TargetRoot, Hasher);
        _pipeline = new BackupPipeline(
            new DirectoryFileSystemScanner(),
            Hasher,
            contentStore,
            _repository,
            new FileRunLock());
    }

    [IterationCleanup]
    public void CleanupIteration() => _repository.Dispose();

    [GlobalCleanup]
    public void Cleanup()
    {
        _repository?.Dispose();
        _repository = null!;
        CleanupWorkspace();
    }

    [Benchmark]
    public BackupRunResult RunFirstBackup() => _pipeline.Run(_profile);
}
