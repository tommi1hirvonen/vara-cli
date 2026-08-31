using Vara.Core.IO;
using Xunit;

namespace Vara.Core.Tests.IO;

public class TeeStreamTests
{
    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void Reading_through_the_tee_yields_the_source_content_unchanged()
    {
        var content = RandomBytes(50_000);
        using var source = new MemoryStream(content);
        using var sink = new MemoryStream();
        using var tee = new TeeStream(source, sink);

        using var readBack = new MemoryStream();
        tee.CopyTo(readBack);

        Assert.Equal(content, readBack.ToArray());
    }

    [Fact]
    public void Reading_through_the_tee_writes_the_same_bytes_to_the_sink()
    {
        var content = RandomBytes(50_000);
        using var source = new MemoryStream(content);
        using var sink = new MemoryStream();
        using var tee = new TeeStream(source, sink);

        tee.CopyTo(Stream.Null);

        Assert.Equal(content, sink.ToArray());
    }

    [Fact]
    public void TotalBytesCopied_reflects_the_full_length_once_fully_read()
    {
        var content = RandomBytes(12_345);
        using var source = new MemoryStream(content);
        using var sink = new MemoryStream();
        using var tee = new TeeStream(source, sink);

        tee.CopyTo(Stream.Null);

        Assert.Equal(content.LongLength, tee.TotalBytesCopied);
    }

    [Fact]
    public void Empty_source_copies_nothing_and_never_invokes_the_callback()
    {
        using var source = new MemoryStream([]);
        using var sink = new MemoryStream();
        var invoked = false;
        using var tee = new TeeStream(source, sink, _ => invoked = true);

        tee.CopyTo(Stream.Null);

        Assert.Equal(0, tee.TotalBytesCopied);
        Assert.False(invoked);
    }

    [Fact]
    public void The_callback_reports_each_chunk_with_a_strictly_increasing_cumulative_total()
    {
        var content = RandomBytes(200_000);
        using var source = new MemoryStream(content);
        using var sink = new MemoryStream();
        var reportedTotal = 0L;
        var callCount = 0;
        using var tee = new TeeStream(source, sink, bytes =>
        {
            callCount++;
            reportedTotal += bytes;
            Assert.True(reportedTotal <= content.LongLength);
        });

        // A small destination buffer forces CopyTo to issue many small reads against the
        // tee, exercising the callback many times rather than in one large chunk.
        tee.CopyTo(Stream.Null, bufferSize: 4096);

        Assert.True(callCount > 1, "expected more than one chunk callback for a large source");
        Assert.Equal(content.LongLength, reportedTotal);
        Assert.Equal(content.LongLength, tee.TotalBytesCopied);
    }

    [Theory]
    [InlineData(1, 4096)]
    [InlineData(4096, 1)]
    [InlineData(8192, 8192)]
    [InlineData(1, 1)]
    public void Composes_correctly_with_a_BufferedStream_wrapping_the_source_across_buffer_size_combinations(
        int bufferedStreamBufferSize, int readerChunkSize)
    {
        var content = RandomBytes(100_000);
        using var rawSource = new MemoryStream(content);
        using var bufferedSource = new BufferedStream(rawSource, bufferedStreamBufferSize);
        using var sink = new MemoryStream();
        using var tee = new TeeStream(bufferedSource, sink);

        using var readBack = new MemoryStream();
        tee.CopyTo(readBack, readerChunkSize);

        Assert.Equal(content, readBack.ToArray());
        Assert.Equal(content, sink.ToArray());
        Assert.Equal(content.LongLength, tee.TotalBytesCopied);
    }
}
