using BenchmarkDotNet.Attributes;
using Vara.Application.Backup;
using Vara.Core.Abstractions;
using Vara.Core.Hashing;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;

namespace Vara.Benchmarks;

public enum DiffScenario
{
    Unchanged,
    Added,
    Changed,
    Deleted,
    Mixed,
}

[MemoryDiagnoser]
public class BackupDifferBenchmarks
{
    private IReadOnlyList<ScanItem> _scanned = [];
    private IReadOnlyDictionary<string, CurrentFileState> _currentState =
        new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase);

    [Params(256, 4_096)]
    public int EntryCount { get; set; }

    [Params(DiffScenario.Unchanged, DiffScenario.Added, DiffScenario.Changed, DiffScenario.Deleted, DiffScenario.Mixed)]
    public DiffScenario Scenario { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var scanned = new List<ScanItem>(EntryCount);
        var currentState = new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase);
        var modifiedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < EntryCount; i++)
        {
            var relativePath = $"source\\folder-{i % 32:D2}\\file-{i:D6}.bin";
            var absolutePath = Path.Combine(Path.GetTempPath(), "vara-benchmark-source", $"file-{i:D6}.bin");
            var state = Scenario == DiffScenario.Mixed
                ? (i % 4) switch
                {
                    0 => ChangeKind.Unchanged,
                    1 => ChangeKind.Added,
                    2 => ChangeKind.Changed,
                    _ => ChangeKind.Deleted,
                }
                : Scenario switch
                {
                    DiffScenario.Unchanged => ChangeKind.Unchanged,
                    DiffScenario.Added => ChangeKind.Added,
                    DiffScenario.Changed => ChangeKind.Changed,
                    DiffScenario.Deleted => ChangeKind.Deleted,
                    _ => throw new ArgumentOutOfRangeException(nameof(Scenario)),
                };

            if (state is ChangeKind.Unchanged or ChangeKind.Added or ChangeKind.Changed)
            {
                scanned.Add(ScanItem.File(
                    relativePath,
                    absolutePath,
                    size: state == ChangeKind.Changed ? 4_097 : 4_096,
                    modifiedAt));
            }

            if (state is ChangeKind.Unchanged or ChangeKind.Changed or ChangeKind.Deleted)
            {
                currentState.Add(relativePath, new CurrentFileState(
                    relativePath,
                    "benchmark-hash",
                    4_096,
                    modifiedAt,
                    QuickHash: null,
                    QuickHashScheme: null));
            }
        }

        _scanned = scanned;
        _currentState = currentState;
    }

    [Benchmark]
    public DiffResult Diff() => BackupDiffer.Diff(_scanned, _currentState, Array.Empty<ScanFailure>());

    private enum ChangeKind
    {
        Unchanged,
        Added,
        Changed,
        Deleted,
    }
}

[MemoryDiagnoser]
public class BackupPlannerBenchmarks
{
    private readonly XxHash128Hasher _hasher = new();
    private string _scratchDirectory = string.Empty;
    private DiffResult _diff = new([], []);
    private IReadOnlyDictionary<string, CurrentFileState> _currentState =
        new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase);

    [Params(262_144, 4_194_304)]
    public int ContentSize { get; set; }

    [Params(true, false)]
    public bool IsMoveMatch { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _scratchDirectory = BenchmarkFiles.CreateScratchDirectory();
        var priorContent = BenchmarkFiles.CreateDeterministicBytes(ContentSize, salt: 1);
        var addedContent = IsMoveMatch
            ? priorContent
            : BenchmarkFiles.CreateDeterministicBytes(ContentSize, salt: 2);
        var addedPath = Path.Combine(_scratchDirectory, "new-location.bin");
        File.WriteAllBytes(addedPath, addedContent);

        using var priorStream = new MemoryStream(priorContent, writable: false);
        var fullHash = _hasher.ComputeHash(priorStream);
        using var quickStream = new MemoryStream(priorContent, writable: false);
        var quickHash = new StreamingContentSignature(quickStream, _hasher).ComputeQuickHash();

        const string oldRelativePath = "source\\old-location.bin";
        const string addedRelativePath = "source\\new-location.bin";
        var fileInfo = new FileInfo(addedPath);
        var scannedFile = ScanItem.File(addedRelativePath, addedPath, fileInfo.Length, fileInfo.LastWriteTimeUtc);
        _diff = new DiffResult(
            [new PendingChange(scannedFile, PendingChangeKind.Added)],
            [oldRelativePath]);
        _currentState = new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase)
        {
            [oldRelativePath] = new CurrentFileState(
                oldRelativePath,
                fullHash,
                ContentSize,
                fileInfo.LastWriteTimeUtc,
                quickHash,
                QuickHashPolicy.CurrentScheme),
        };
    }

    [GlobalCleanup]
    public void Cleanup() => BenchmarkFiles.DeleteDirectory(_scratchDirectory);

    [Benchmark]
    public BackupPlan PlanMoveDetection() => new BackupPlanner(_hasher, maxDegreeOfParallelism: 1).Plan(_diff, _currentState);
}
