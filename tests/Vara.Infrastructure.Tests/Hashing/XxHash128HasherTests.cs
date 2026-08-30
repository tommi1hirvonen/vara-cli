using System.Text;
using Vara.Infrastructure.Hashing;
using Xunit;

namespace Vara.Infrastructure.Tests.Hashing;

public class XxHash128HasherTests
{
    private readonly XxHash128Hasher _hasher = new();

    [Fact]
    public void Identical_content_produces_the_same_hash()
    {
        using var a = new MemoryStream(Encoding.UTF8.GetBytes("hello world"));
        using var b = new MemoryStream(Encoding.UTF8.GetBytes("hello world"));

        Assert.Equal(_hasher.ComputeHash(a), _hasher.ComputeHash(b));
    }

    [Fact]
    public void Different_content_produces_different_hashes()
    {
        using var a = new MemoryStream(Encoding.UTF8.GetBytes("hello world"));
        using var b = new MemoryStream(Encoding.UTF8.GetBytes("goodbye world"));

        Assert.NotEqual(_hasher.ComputeHash(a), _hasher.ComputeHash(b));
    }

    [Fact]
    public void Hash_is_lowercase_hex()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello world"));

        var hash = _hasher.ComputeHash(stream);

        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.Matches("^[0-9a-f]+$", hash);
    }
}
