using BenchmarkDotNet.Attributes;
using Vara.Core.Hashing;
using Vara.Infrastructure.Hashing;

namespace Vara.Benchmarks;

[MemoryDiagnoser]
public class StreamingSignatureBenchmarks
{
    private readonly XxHash128Hasher _hasher = new();
    private byte[] _content = [];
    private byte[] _prefix = [];
    private byte[] _remainder = [];
    private byte[] _prefixReadBuffer = [];
    private byte[] _boundaryReadBuffer = new byte[8_192];
    private string _scratchDirectory = string.Empty;
    private string _filePath = string.Empty;

    [Params(262_144, 4_194_304)]
    public int ContentSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _content = BenchmarkFiles.CreateDeterministicBytes(ContentSize, salt: 17);
        _prefix = _content[..QuickHashPolicy.WindowSizeBytes];
        _remainder = _content[QuickHashPolicy.WindowSizeBytes..];
        _prefixReadBuffer = new byte[_prefix.Length];
        _scratchDirectory = BenchmarkFiles.CreateScratchDirectory();
        _filePath = Path.Combine(_scratchDirectory, "content.bin");
        File.WriteAllBytes(_filePath, _content);
    }

    [GlobalCleanup]
    public void Cleanup() => BenchmarkFiles.DeleteDirectory(_scratchDirectory);

    [Benchmark]
    public int PrefixReplayStreamCopyToNull()
    {
        using var remainder = new MemoryStream(_remainder, writable: false);
        using var replay = new PrefixReplayStream(_prefix, remainder);
        replay.CopyTo(Stream.Null, bufferSize: 81_920);
        return _content.Length;
    }

    [Benchmark]
    public int PrefixReplayStreamReadPrefix()
    {
        using var remainder = new MemoryStream(_remainder, writable: false);
        using var replay = new PrefixReplayStream(_prefix, remainder);
        return replay.Read(_prefixReadBuffer, 0, _prefixReadBuffer.Length);
    }

    [Benchmark]
    public int PrefixReplayStreamReadAcrossBoundary()
    {
        using var remainder = new MemoryStream(_remainder, writable: false);
        using var replay = new PrefixReplayStream(_prefix, remainder);
        var readBeforeBoundary = replay.Read(_prefixReadBuffer, 0, _prefixReadBuffer.Length - 1);
        var readLastPrefixByte = replay.Read(_boundaryReadBuffer, 0, _boundaryReadBuffer.Length);
        var readRemainder = replay.Read(_boundaryReadBuffer, 0, _boundaryReadBuffer.Length);
        return readBeforeBoundary + readLastPrefixByte + readRemainder;
    }

    [Benchmark]
    public string ComputeQuickHash()
    {
        using var source = new MemoryStream(_content, writable: false);
        return new StreamingContentSignature(source, _hasher).ComputeQuickHash();
    }

    [Benchmark]
    public string ComputeQuickAndFullHash()
    {
        using var source = new MemoryStream(_content, writable: false);
        var signature = new StreamingContentSignature(source, _hasher);
        signature.ComputeQuickHash();
        return signature.ContinueToFullHash();
    }

    [Benchmark]
    public string ComputeQuickAndFullHashFromFile()
    {
        using var source = File.OpenRead(_filePath);
        var signature = new StreamingContentSignature(source, _hasher);
        signature.ComputeQuickHash();
        return signature.ContinueToFullHash();
    }
}
