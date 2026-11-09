namespace Vara.Engine

open System
open System.IO

/// A forward-only, read-only stream that yields a buffered prefix and then the rest of an inner stream: the whole
/// content of a stream whose first bytes were already read to hash them. Does not own `inner`.
type PrefixReplayStream(prefix: byte[], inner: Stream) =
    inherit Stream()
    let mutable position = 0

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Read(buffer: Span<byte>) : int =
        if position < prefix.Length then
            let count = Math.Min(prefix.Length - position, buffer.Length)
            ReadOnlySpan<byte>(prefix, position, count).CopyTo buffer
            position <- position + count
            count
        else
            inner.Read buffer

    override this.Read(buffer: byte[], offset: int, count: int) : int =
        Stream.ValidateBufferArguments(buffer, offset, count)
        this.Read(Span<byte>(buffer, offset, count))

    override _.Flush() = ()
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_: int64) = raise (NotSupportedException())
    override _.Write(_: byte[], _: int, _: int) = raise (NotSupportedException())

/// A read-only stream that writes every byte it reads from `source` into `sink` and reports each chunk's size, so
/// one forward pass (a hash) also copies the content and reports progress. Owns neither stream.
type TeeStream(source: Stream, sink: Stream, onBytesCopied: (int64 -> unit) voption) =
    inherit Stream()
    let mutable total = 0L

    /// Bytes read from the source and written to the sink so far.
    member _.TotalBytesCopied = total

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Read(buffer: Span<byte>) : int =
        let read = source.Read buffer

        if read > 0 then
            sink.Write(Span<byte>.op_Implicit (buffer.Slice(0, read)))
            total <- total + int64 read

            match onBytesCopied with
            | ValueSome report -> report (int64 read)
            | ValueNone -> ()

        read

    override this.Read(buffer: byte[], offset: int, count: int) : int =
        Stream.ValidateBufferArguments(buffer, offset, count)
        this.Read(Span<byte>(buffer, offset, count))

    override _.Flush() = sink.Flush()
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_: int64) = raise (NotSupportedException())
    override _.Write(_: byte[], _: int, _: int) = raise (NotSupportedException())
