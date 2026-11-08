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

    /// The hash text of a byte prefix, as recorded in a quick signature.
    let hashPrefix (prefix: byte[]) : string =
        let hash = XxHash128()
        hash.Append(ReadOnlySpan<byte> prefix)
        toHex hash
