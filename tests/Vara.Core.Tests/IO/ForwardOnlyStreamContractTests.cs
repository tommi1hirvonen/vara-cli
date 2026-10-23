using Vara.Core.Hashing;
using Vara.Core.IO;
using Xunit;

namespace Vara.Core.Tests.IO;

/// <summary>
/// Both wrapper streams are forward-only readers; everything else they expose must refuse
/// loudly rather than silently misbehave when a consumer assumes a seekable/writable stream.
/// </summary>
public class ForwardOnlyStreamContractTests
{
    public static TheoryData<string> StreamKinds => ["tee", "prefix-replay"];

    private static Stream Create(string kind) => kind switch
    {
        "tee" => new TeeStream(new MemoryStream([1, 2, 3]), new MemoryStream()),
        "prefix-replay" => new PrefixReplayStream([1], new MemoryStream([2, 3])),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(StreamKinds))]
    public void Is_readable_but_neither_seekable_nor_writable(string kind)
    {
        using var stream = Create(kind);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    [Theory]
    [MemberData(nameof(StreamKinds))]
    public void Length_position_seek_setlength_and_write_are_unsupported(string kind)
    {
        using var stream = Create(kind);

        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    [Theory]
    [MemberData(nameof(StreamKinds))]
    public void Array_based_reads_return_the_same_content_as_span_reads(string kind)
    {
        using var stream = Create(kind);
        var buffer = new byte[8];

        var read = 0;
        int chunk;
        while ((chunk = stream.Read(buffer, read, 1)) > 0)
        {
            read += chunk;
        }

        Assert.Equal([1, 2, 3], buffer[..read]);
    }

    [Fact]
    public void Tee_flush_flushes_the_sink()
    {
        var sink = new FlushCountingStream();
        using var tee = new TeeStream(new MemoryStream([1]), sink);

        tee.Flush();

        Assert.Equal(1, sink.FlushCount);
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override void Flush() => FlushCount++;
    }
}
