using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Vara.Application.Backup;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Engine;
using Vara.Infrastructure.Concurrency;
using Vara.Infrastructure.FileSystem;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;

namespace Vara.Benchmarks;

// C# engine as the baseline, F# engine (Vara.Engine) as the comparison, on the same fixtures and parameters as the
// C# benchmarks they mirror. The F# side's setup and cleanup live in Vara.Benchmarks.FSharp.

[MemoryDiagnoser]
public class ScanLanguageComparison
{
    private string _scratchDirectory = string.Empty;
    private string _sourceRoot = string.Empty;
    private Source _source = null!;

    [Params(256, 4_096)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _scratchDirectory = BenchmarkFiles.CreateScratchDirectory();
        _sourceRoot = Path.Combine(_scratchDirectory, "source");
        Directory.CreateDirectory(_sourceRoot);

        for (var i = 0; i < FileCount; i++)
        {
            var directory = Path.Combine(_sourceRoot, $"folder-{i % 32:D2}");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(
                Path.Combine(directory, $"file-{i:D6}.bin"),
                BenchmarkFiles.CreateDeterministicBytes(128, i));
        }

        _source = new Source(_sourceRoot);
    }

    [GlobalCleanup]
    public void Cleanup() => BenchmarkFiles.DeleteDirectory(_scratchDirectory);

    [Benchmark(Baseline = true)]
    public int CSharp()
    {
        var result = new DirectoryFileSystemScanner().Scan([_source]);
        var count = 0;
        foreach (var _ in result.Entries)
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public int FSharp() => FSharpSide.scanAndEnumerate(_sourceRoot);
}

[MemoryDiagnoser]
public class ExecutorLanguageComparison : BackupBenchmarkBase
{
    private string _fsharpTarget = string.Empty;
    private FileSystemContentStore _contentStore = null!;
    private SqliteSnapshotRepository _repository = null!;
    private IManifestBatch _manifestBatch = null!;
    private BackupExecutor _executor = null!;
    private BackupPlan _plan = null!;
    private long _snapshotId;
    private DateTimeOffset _recordedAt;
    private FSharpSide.ExecutorFixture _fixture = null!;
    private Task<ExecutionEnd>? _fsharpRun;

    [Params(1, 4)]
    public int TransferConcurrency { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        CreateSourceFixture();
        _fsharpTarget = Path.Combine(ScratchDirectory, "target-fsharp");
    }

    [IterationSetup]
    public void SetupIteration()
    {
        DisposeIteration();
        ResetTarget();
        BenchmarkFiles.DeleteDirectory(_fsharpTarget);
        Directory.CreateDirectory(_fsharpTarget);

        _contentStore = new FileSystemContentStore(TargetRoot, Hasher);
        _contentStore.ProbeHardlinkSupport();
        _repository = new SqliteSnapshotRepository(DatabasePath);
        _recordedAt = DateTimeOffset.UtcNow;
        _snapshotId = _repository.BeginSnapshot(_recordedAt);
        _manifestBatch = _repository.BeginManifestBatch();
        _executor = new BackupExecutor(_contentStore, _repository, Hasher, maxDegreeOfParallelism: TransferConcurrency);

        var operations = new List<PlannedOperation>(FileCount);
        long totalBytes = 0;
        for (var i = 0; i < FileCount; i++)
        {
            var path = Path.Combine(SourceRoot, $"file-{i:D4}.bin");
            var file = new FileInfo(path);
            operations.Add(new AddOperation($"source\\file-{i:D4}.bin", path, file.Length, file.LastWriteTimeUtc));
            totalBytes += file.Length;
        }

        _plan = new BackupPlan(operations, totalBytes);
        _fixture = FSharpSide.prepareExecutor(SourceRoot, _fsharpTarget, FileCount, TransferConcurrency);
    }

    [IterationCleanup]
    public void CleanupIteration() => DisposeIteration();

    [GlobalCleanup]
    public void Cleanup()
    {
        DisposeIteration();
        CleanupWorkspace();
    }

    [Benchmark(Baseline = true)]
    public Task<ExecutionOutcome> CSharp() =>
        _executor.ExecuteAsync(_snapshotId, _recordedAt, _plan, manifestBatch: _manifestBatch);

    [Benchmark]
    public Task<ExecutionEnd> FSharp() => _fsharpRun = FSharpSide.execute(_fixture);

    private void DisposeIteration()
    {
        if (_manifestBatch is not null)
        {
            _manifestBatch.Commit();
            _manifestBatch.Dispose();
            _manifestBatch = null!;
        }

        _repository?.Dispose();
        _repository = null!;

        if (_fixture is not null)
        {
            var ended = _fsharpRun is { IsCompletedSuccessfully: true }
                ? Microsoft.FSharp.Core.FSharpValueOption<ExecutionEnd>.NewValueSome(_fsharpRun.Result)
                : Microsoft.FSharp.Core.FSharpValueOption<ExecutionEnd>.ValueNone;
            FSharpSide.closeExecutor(_fixture, ended);
            _fixture = null!;
            _fsharpRun = null;
        }
    }
}

[MemoryDiagnoser]
public class PipelineLanguageComparison : BackupBenchmarkBase
{
    private string _fsharpTarget = string.Empty;
    private SqliteSnapshotRepository _repository = null!;
    private BackupPipeline _pipeline = null!;
    private Profile _profile = null!;
    private Vara.Domain.Profile _fsharpProfile = null!;

    [GlobalSetup]
    public void Setup()
    {
        CreateSourceFixture();
        _fsharpTarget = Path.Combine(ScratchDirectory, "target-fsharp");
        _profile = new Profile("benchmark", TargetRoot, [new Source(SourceRoot)], retention: null);
        _fsharpProfile = FSharpSide.profileOf(SourceRoot, _fsharpTarget);
    }

    [IterationSetup]
    public void SetupIteration()
    {
        _repository?.Dispose();
        _repository = null!;
        ResetTarget();
        BenchmarkFiles.DeleteDirectory(_fsharpTarget);
        Directory.CreateDirectory(_fsharpTarget);

        _repository = new SqliteSnapshotRepository(DatabasePath);
        _pipeline = new BackupPipeline(
            new DirectoryFileSystemScanner(),
            Hasher,
            new FileSystemContentStore(TargetRoot, Hasher),
            _repository,
            new FileRunLock());
    }

    [IterationCleanup]
    public void CleanupIteration()
    {
        _repository?.Dispose();
        SqliteConnection.ClearAllPools();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _repository?.Dispose();
        _repository = null!;
        CleanupWorkspace();
    }

    [Benchmark(Baseline = true)]
    public Task<BackupRunResult> CSharp() => _pipeline.RunAsync(_profile);

    [Benchmark]
    public Task<int> FSharp() => FSharpSide.runFirstBackup(_fsharpProfile);
}
