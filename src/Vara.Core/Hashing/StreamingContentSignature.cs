using Vara.Core.Abstractions;

namespace Vara.Core.Hashing;

/// <summary>
/// Computes a bounded-prefix "quick hash" and, on demand, the full-content hash of a
/// stream in a single forward pass over its source - no re-opening or re-reading bytes
/// already consumed. Reads at most <see cref="QuickHashPolicy.WindowSizeBytes"/> bytes
/// to produce the quick hash; a caller only needs to continue past that point (via
/// <see cref="ContinueToFullHash"/> or <see cref="ReplayFromStart"/>) when the quick
/// hash could plausibly matter, at which point the already-read prefix bytes are
/// replayed from memory rather than re-read from <paramref name="source"/>. Does not
/// change <see cref="IHasher"/>'s existing single-call contract - both hashes are still
/// produced via ordinary <see cref="IHasher.ComputeHash"/> calls, just over streams that
/// avoid a redundant physical re-read.
/// </summary>
public sealed class StreamingContentSignature(Stream source, IHasher hasher)
{
    private byte[]? _prefix;

    /// <summary>
    /// Reads up to <see cref="QuickHashPolicy.WindowSizeBytes"/> bytes from
    /// <paramref name="source"/> (fewer if the source is shorter) and returns their
    /// hash. Must be called exactly once, before <see cref="ContinueToFullHash"/> or
    /// <see cref="ReplayFromStart"/>.
    /// </summary>
    public string ComputeQuickHash()
    {
        if (_prefix is not null)
        {
            throw new InvalidOperationException($"{nameof(ComputeQuickHash)} can only be called once.");
        }

        _prefix = ReadUpTo(source, QuickHashPolicy.WindowSizeBytes);
        return hasher.ComputeHash(new MemoryStream(_prefix, writable: false));
    }

    /// <summary>
    /// A stream yielding the buffered prefix followed by the remainder of
    /// <paramref name="source"/> - the full original content, without re-reading the
    /// prefix bytes from <paramref name="source"/>. Can only be consumed once; must be
    /// called after <see cref="ComputeQuickHash"/>.
    /// </summary>
    public Stream ReplayFromStart()
    {
        if (_prefix is null)
        {
            throw new InvalidOperationException($"{nameof(ComputeQuickHash)} must be called before {nameof(ReplayFromStart)}.");
        }

        return new PrefixReplayStream(_prefix, source);
    }

    /// <summary>
    /// Reads the remainder of <paramref name="source"/> and returns the hash of the full
    /// content (prefix + remainder) - equivalent to what a fresh
    /// <see cref="IHasher.ComputeHash"/> call from the start of the source would
    /// produce, but without re-reading the prefix bytes already consumed by
    /// <see cref="ComputeQuickHash"/>.
    /// </summary>
    public string ContinueToFullHash() => hasher.ComputeHash(ReplayFromStart());

    private static byte[] ReadUpTo(Stream stream, int maxBytes)
    {
        var buffer = new byte[maxBytes];
        var totalRead = 0;
        int read;
        while (totalRead < maxBytes && (read = stream.Read(buffer, totalRead, maxBytes - totalRead)) > 0)
        {
            totalRead += read;
        }

        return totalRead == maxBytes ? buffer : buffer[..totalRead];
    }
}
