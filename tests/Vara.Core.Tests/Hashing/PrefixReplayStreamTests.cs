using Vara.Core.Hashing;
using Xunit;

namespace Vara.Core.Tests.Hashing;

public class PrefixReplayStreamTests
{
    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void Read_with_span_replays_prefix_then_reads_remainder()
    {
        var prefix = RandomBytes(500);
        var remainder = RandomBytes(1500);
        var expected = prefix.Concat(remainder).ToArray();

        using var inner = new MemoryStream(remainder);
        using var stream = new PrefixReplayStream(prefix, inner);

        Span<byte> buffer = stackalloc byte[128];
        using var result = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            result.Write(buffer[..read]);
        }

        Assert.Equal(expected, result.ToArray());
    }

    [Fact]
    public void Read_with_byte_array_replays_prefix_then_reads_remainder()
    {
        var prefix = RandomBytes(300);
        var remainder = RandomBytes(700);
        var expected = prefix.Concat(remainder).ToArray();

        using var inner = new MemoryStream(remainder);
        using var stream = new PrefixReplayStream(prefix, inner);

        var buffer = new byte[64];
        using var result = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.Write(buffer, 0, read);
        }

        Assert.Equal(expected, result.ToArray());
    }
}
