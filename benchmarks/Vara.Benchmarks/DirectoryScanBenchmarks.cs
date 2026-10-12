using BenchmarkDotNet.Attributes;
using Vara.Core.Configuration;
using Vara.Infrastructure.FileSystem;

namespace Vara.Benchmarks;

[MemoryDiagnoser]
public class DirectoryScanBenchmarks
{
    private string _scratchDirectory = string.Empty;
    private Source _source = null!;

    [Params(256, 4_096)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _scratchDirectory = BenchmarkFiles.CreateScratchDirectory();
        var sourceRoot = Path.Combine(_scratchDirectory, "source");
        Directory.CreateDirectory(sourceRoot);

        for (var i = 0; i < FileCount; i++)
        {
            var directory = Path.Combine(sourceRoot, $"folder-{i % 32:D2}");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(
                Path.Combine(directory, $"file-{i:D6}.bin"),
                BenchmarkFiles.CreateDeterministicBytes(128, i));
        }

        _source = new Source(sourceRoot);
    }

    [GlobalCleanup]
    public void Cleanup() => BenchmarkFiles.DeleteDirectory(_scratchDirectory);

    [Benchmark]
    public int ScanAndEnumerate()
    {
        var result = new DirectoryFileSystemScanner().Scan([_source]);
        var count = 0;
        foreach (var _ in result.Entries)
        {
            count++;
        }

        return count;
    }
}
