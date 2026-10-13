using BenchmarkDotNet.Attributes;
using Vara.Core.IO;
using Vara.Infrastructure.Hashing;

namespace Vara.Benchmarks;

[MemoryDiagnoser]
public class ContentStoreBufferBenchmarks
{
    private readonly XxHash128Hasher _hasher = new();
    private string _scratchDirectory = string.Empty;
    private string _sourcePath = string.Empty;
    private string _destinationPath = string.Empty;

    [Params(65_536, 1_048_576)]
    public int ContentSize { get; set; }

    [Params(4_096, 16_384, 65_536, 81_920, 262_144, 1_048_576)]
    public int BufferSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _scratchDirectory = BenchmarkFiles.CreateScratchDirectory();
        _sourcePath = Path.Combine(_scratchDirectory, "source.bin");
        _destinationPath = Path.Combine(_scratchDirectory, "stored.bin");
        File.WriteAllBytes(_sourcePath, BenchmarkFiles.CreateDeterministicBytes(ContentSize, salt: 23));
    }

    [GlobalCleanup]
    public void Cleanup() => BenchmarkFiles.DeleteDirectory(_scratchDirectory);

    [Benchmark]
    public string HashWhileCopyingToBufferedFile()
    {
        using var source = File.OpenRead(_sourcePath);
        using var destination = new FileStream(_destinationPath, FileMode.Create, FileAccess.Write);
        var bufferedSource = new BufferedStream(source, BufferSize);
        using var bufferedDestination = new BufferedStream(destination, BufferSize);
        using var tee = new TeeStream(bufferedSource, bufferedDestination);
        return _hasher.ComputeHash(tee);
    }
}
