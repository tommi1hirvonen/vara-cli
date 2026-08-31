namespace Vara.Core.IO;

/// <summary>
/// A read-only stream that mirrors every byte read from <paramref name="source"/> into
/// <paramref name="sink"/> as it is read, invoking <paramref name="onBytesCopied"/> with
/// each chunk's size. Lets a single forward pass over <paramref name="source"/> - such as
/// a hash computation - simultaneously write the same bytes to <paramref name="sink"/> and
/// report incremental progress, instead of requiring a separate write pass followed by a
/// separate re-read pass to hash or copy the same content (stream-large-file-transfer-progress
/// change's design.md). Does not own <paramref name="source"/> or <paramref name="sink"/> -
/// the caller remains responsible for disposing both.
/// </summary>
public sealed class TeeStream(Stream source, Stream sink, Action<long>? onBytesCopied = null) : Stream
{
    /// <summary>Cumulative number of bytes read from <paramref name="source"/> (and written to <paramref name="sink"/>) so far.</summary>
    public long TotalBytesCopied { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (read > 0)
        {
            sink.Write(buffer, offset, read);
            TotalBytesCopied += read;
            onBytesCopied?.Invoke(read);
        }

        return read;
    }

    public override void Flush() => sink.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
