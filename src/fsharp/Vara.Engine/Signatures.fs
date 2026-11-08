namespace Vara.Engine

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Vara.Domain

/// Reading content signatures: the prefix hash that rules a move candidate out cheaply, and the full hash that
/// confirms a move. A single forward read serves both, because the prefix bytes are replayed from memory.
module Signatures =
    /// Reads up to the quick-signature window from the stream, fewer if the stream is shorter.
    let readPrefix (stream: Stream) : byte[] =
        let window = QuickSignature.windowSizeBytes
        let buffer = Array.zeroCreate<byte> window
        let mutable total = 0
        let mutable read = stream.Read(buffer, 0, window)

        while read > 0 do
            total <- total + read
            read <- if total < window then stream.Read(buffer, total, window - total) else 0

        if total = window then buffer else Array.sub buffer 0 total

    /// The signature recorded for content whose first bytes are `prefix`.
    let quickOf (prefix: byte[]) : QuickSignature =
        { QuickHash = Hashing.hashPrefix prefix
          QuickScheme = QuickSignature.currentScheme }

    /// The exact content hash of an added file, when a move from one of its same-size candidates is still possible
    /// after its prefix was compared. None when the prefix rules every candidate out, or the file cannot be read, in
    /// which case it is planned as a plain add and the transfer reports any real failure itself.
    let private fullHashIfMovePossible
        (openRead: string -> Stream)
        (candidates: (MirrorPath * FileContent) list)
        (file: ScannedFile)
        : ContentHash voption =
        try
            use stream = openRead (SourcePath.value file.FileSource)
            let prefix = readPrefix stream

            if Planning.needsFullHash candidates (Hashing.hashPrefix prefix) then
                use replay = new PrefixReplayStream(prefix, stream)
                ValueSome(Hashing.hashStream replay)
            else
                ValueNone
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ValueNone

    /// Reads, in parallel, the signature of every added file that shares a size with a deleted candidate, keyed by
    /// mirror path. Only a file whose prefix could still match a candidate is read to the end. `openRead` is the
    /// read effect, so a test can observe how much is read.
    let readFullHashes
        (openRead: string -> Stream)
        (maxParallelism: int)
        (candidates: MoveCandidates)
        (adds: ScannedFile list)
        : Dictionary<string, ContentHash> =
        let files = Array.ofList adds
        // One slot per file, each written by exactly one iteration, so no synchronization is needed.
        let slots = Array.create files.Length ValueNone
        let options = ParallelOptions(MaxDegreeOfParallelism = maxParallelism)

        Parallel.For(
            0,
            files.Length,
            options,
            fun i -> slots[i] <- fullHashIfMovePossible openRead candidates[files[i].Size] files[i]
        )
        |> ignore

        let hashes = Dictionary<string, ContentHash>(StringComparer.OrdinalIgnoreCase)

        for i in 0 .. files.Length - 1 do
            match slots[i] with
            | ValueSome hash -> hashes[MirrorPath.value files[i].FilePath] <- hash
            | ValueNone -> ()

        hashes

    /// The real read effect.
    let openFile (path: string) : Stream = File.OpenRead path
