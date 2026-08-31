using System.Security.Cryptography;
using Vara.Core.Abstractions;
using Vara.Core.Hashing;
using Xunit;

namespace Vara.Core.Tests.Hashing;

public class StreamingContentSignatureTests
{
    [Fact]
    public void ComputeQuickHash_covers_only_the_leading_window_for_a_longer_stream()
    {
        var content = RandomBytes(QuickHashPolicy.WindowSizeBytes * 2);
        var hasher = new Sha256Hasher();

        using var stream = new MemoryStream(content);
        var signature = new StreamingContentSignature(stream, hasher);
        var quickHash = signature.ComputeQuickHash();

        var expectedPrefixHash = hasher.ComputeHash(new MemoryStream(content[..QuickHashPolicy.WindowSizeBytes]));
        Assert.Equal(expectedPrefixHash, quickHash);
    }

    [Fact]
    public void ComputeQuickHash_covers_the_whole_content_when_shorter_than_the_window()
    {
        var content = RandomBytes(QuickHashPolicy.WindowSizeBytes / 4);
        var hasher = new Sha256Hasher();

        using var stream = new MemoryStream(content);
        var signature = new StreamingContentSignature(stream, hasher);
        var quickHash = signature.ComputeQuickHash();

        var expectedHash = hasher.ComputeHash(new MemoryStream(content));
        Assert.Equal(expectedHash, quickHash);
    }

    [Fact]
    public void ComputeQuickHash_covers_the_whole_content_when_exactly_the_window_size()
    {
        var content = RandomBytes(QuickHashPolicy.WindowSizeBytes);
        var hasher = new Sha256Hasher();

        using var stream = new MemoryStream(content);
        var signature = new StreamingContentSignature(stream, hasher);
        var quickHash = signature.ComputeQuickHash();

        var expectedHash = hasher.ComputeHash(new MemoryStream(content));
        Assert.Equal(expectedHash, quickHash);
    }

    [Fact]
    public void ContinueToFullHash_matches_a_fresh_hash_of_the_entire_content()
    {
        var content = RandomBytes(QuickHashPolicy.WindowSizeBytes * 3 + 12345);
        var hasher = new Sha256Hasher();

        using var stream = new MemoryStream(content);
        var signature = new StreamingContentSignature(stream, hasher);
        signature.ComputeQuickHash();
        var fullHash = signature.ContinueToFullHash();

        var expectedFullHash = hasher.ComputeHash(new MemoryStream(content));
        Assert.Equal(expectedFullHash, fullHash);
    }

    [Fact]
    public void ReplayFromStart_yields_the_full_original_content_byte_for_byte()
    {
        var content = RandomBytes(QuickHashPolicy.WindowSizeBytes * 2 + 777);
        var hasher = new Sha256Hasher();

        using var stream = new MemoryStream(content);
        var signature = new StreamingContentSignature(stream, hasher);
        signature.ComputeQuickHash();

        using var replay = signature.ReplayFromStart();
        using var buffer = new MemoryStream();
        replay.CopyTo(buffer);

        Assert.Equal(content, buffer.ToArray());
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Deterministic content-based fake hasher (SHA-256, hex) - no production dependency.</summary>
    private sealed class Sha256Hasher : IHasher
    {
        public string ComputeHash(Stream content)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        }
    }
}
