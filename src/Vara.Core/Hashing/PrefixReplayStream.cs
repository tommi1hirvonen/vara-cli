namespace Vara.Core.Hashing;

/// <summary>
/// A forward-only, read-only stream that first yields a buffered prefix, then delegates
/// reads to an inner stream. Used to reconstruct "the full original content" for a
/// stream whose leading bytes have already been consumed elsewhere (to compute a quick
/// hash), without re-reading those bytes from the original source. Does not own or
/// dispose <paramref name="inner"/> - the caller retains that responsibility.
/// </summary>
internal sealed class PrefixReplayStream(byte[] prefix, Stream inner) : Stream
{
    private int _prefixPosition;

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
        if (_prefixPosition < prefix.Length)
        {
            var remainingPrefix = prefix.Length - _prefixPosition;
            var toCopy = Math.Min(remainingPrefix, count);
            Array.Copy(prefix, _prefixPosition, buffer, offset, toCopy);
            _prefixPosition += toCopy;
            return toCopy;
        }

        return inner.Read(buffer, offset, count);
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
