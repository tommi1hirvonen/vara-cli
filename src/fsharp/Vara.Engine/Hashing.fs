namespace Vara.Engine

open System
open System.IO
open System.IO.Hashing
open Vara.Domain

/// Content hashing with XxHash128: fast and not cryptographic, which suffices for change detection and
/// deduplication where nobody is attacking the hash.
module Hashing =
    let private toHex (hash: XxHash128) : string = Convert.ToHexStringLower(hash.GetCurrentHash())

    /// The hash of everything left in the stream.
    let hashStream (content: Stream) : ContentHash =
        let hash = XxHash128()
        hash.Append content
        ContentHash.tryCreate (toHex hash) |> ValueOption.get

    /// Reads at most this many bytes at a time: under 85,000, so the buffer stays off the large object heap.
    let private chunkSize = 81_920

    /// The hash of everything left in the stream, reporting the size of each chunk as it is read. Chunks are large,
    /// so a progress observer is called once per 80 KiB and not once per 4 KiB of `Append`'s own buffer. The loop
    /// keeps one local counter: it is the reader loop, and stays inside this function.
    let hashStreamReporting (onRead: int64 -> unit) (content: Stream) : ContentHash =
        let hash = XxHash128()
        let buffer = System.Buffers.ArrayPool<byte>.Shared.Rent chunkSize

        try
            let mutable read = content.Read(buffer, 0, chunkSize)

            while read > 0 do
                hash.Append(ReadOnlySpan<byte>(buffer, 0, read))
                onRead (int64 read)
                read <- content.Read(buffer, 0, chunkSize)
        finally
            System.Buffers.ArrayPool<byte>.Shared.Return buffer

        ContentHash.tryCreate (toHex hash) |> ValueOption.get

    /// The hash text of a byte prefix, as recorded in a quick signature.
    let hashPrefix (prefix: byte[]) : string =
        let hash = XxHash128()
        hash.Append(ReadOnlySpan<byte> prefix)
        toHex hash
