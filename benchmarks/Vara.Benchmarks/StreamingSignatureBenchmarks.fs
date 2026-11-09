namespace Vara.Benchmarks

open System.IO
open BenchmarkDotNet.Attributes
open Vara.Domain
open Vara.Engine

/// Prefix replay in isolation, and the quick-hash-only and quick-plus-full-hash flows, over deterministic in-memory
/// content and the real hasher.
[<MemoryDiagnoser>]
type StreamingSignatureBenchmarks() =
    let mutable content: byte[] = [||]
    let mutable prefix: byte[] = [||]
    let mutable remainder: byte[] = [||]
    let mutable prefixReadBuffer: byte[] = [||]
    let boundaryReadBuffer: byte[] = Array.zeroCreate 8192
    let mutable scratchDirectory = ""
    let mutable filePath = ""

    [<Params(262144, 4194304)>]
    member val ContentSize = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() : unit =
        content <- BenchmarkFiles.createDeterministicBytes this.ContentSize 17
        prefix <- Array.sub content 0 QuickSignature.windowSizeBytes
        remainder <- Array.sub content QuickSignature.windowSizeBytes (content.Length - QuickSignature.windowSizeBytes)
        prefixReadBuffer <- Array.zeroCreate prefix.Length
        scratchDirectory <- BenchmarkFiles.createScratchDirectory ()
        filePath <- Path.Combine(scratchDirectory, "content.bin")
        File.WriteAllBytes(filePath, content)

    [<GlobalCleanup>]
    member _.Cleanup() : unit = BenchmarkFiles.deleteDirectory scratchDirectory

    [<Benchmark>]
    member _.PrefixReplayStreamCopyToNull() : int =
        use rest = new MemoryStream(remainder, false)
        use replay = new PrefixReplayStream(prefix, rest)
        replay.CopyTo(Stream.Null, 81920)
        content.Length

    [<Benchmark>]
    member _.PrefixReplayStreamReadPrefix() : int =
        use rest = new MemoryStream(remainder, false)
        use replay = new PrefixReplayStream(prefix, rest)
        replay.Read(prefixReadBuffer, 0, prefixReadBuffer.Length)

    [<Benchmark>]
    member _.PrefixReplayStreamReadAcrossBoundary() : int =
        use rest = new MemoryStream(remainder, false)
        use replay = new PrefixReplayStream(prefix, rest)
        let readBeforeBoundary = replay.Read(prefixReadBuffer, 0, prefixReadBuffer.Length - 1)
        let readLastPrefixByte = replay.Read(boundaryReadBuffer, 0, boundaryReadBuffer.Length)
        let readRemainder = replay.Read(boundaryReadBuffer, 0, boundaryReadBuffer.Length)
        readBeforeBoundary + readLastPrefixByte + readRemainder

    [<Benchmark>]
    member _.ComputeQuickHash() : string =
        use source = new MemoryStream(content, false)
        (Signatures.quickOf (Signatures.readPrefix source)).QuickHash

    [<Benchmark>]
    member _.ComputeQuickAndFullHash() : string =
        use source = new MemoryStream(content, false)
        let signature = Signatures.readPrefix source
        let quick = Signatures.quickOf signature
        use replay = new PrefixReplayStream(signature, source)
        quick.QuickHash + ContentHash.value (Hashing.hashStream replay)

    [<Benchmark>]
    member _.ComputeQuickAndFullHashFromFile() : string =
        use source = File.OpenRead filePath
        let signature = Signatures.readPrefix source
        let quick = Signatures.quickOf signature
        use replay = new PrefixReplayStream(signature, source)
        quick.QuickHash + ContentHash.value (Hashing.hashStream replay)
