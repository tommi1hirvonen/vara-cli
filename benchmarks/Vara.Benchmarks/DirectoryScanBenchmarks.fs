namespace Vara.Benchmarks

open System.IO
open BenchmarkDotNet.Attributes
open Vara.Domain
open Vara.Engine

/// Scans and fully enumerates deterministic temporary trees. The scan is lazy, so consuming the entries is required for a
/// complete timing.
[<MemoryDiagnoser>]
type DirectoryScanBenchmarks() =
    let mutable scratchDirectory = ""
    let mutable source: Source option = None

    [<Params(256, 4096)>]
    member val FileCount = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() : unit =
        scratchDirectory <- BenchmarkFiles.createScratchDirectory ()
        let sourceRoot = Path.Combine(scratchDirectory, "source")
        Directory.CreateDirectory sourceRoot |> ignore

        for i in 0 .. this.FileCount - 1 do
            let directory = Path.Combine(sourceRoot, $"folder-{i % 32:D2}")
            Directory.CreateDirectory directory |> ignore
            File.WriteAllBytes(Path.Combine(directory, $"file-{i:D6}.bin"), BenchmarkFiles.createDeterministicBytes 128 i)

        source <- Some(BenchmarkFiles.sourceOf sourceRoot)

    [<GlobalCleanup>]
    member _.Cleanup() : unit = BenchmarkFiles.deleteDirectory scratchDirectory

    [<Benchmark>]
    member _.ScanAndEnumerate() : int = Scanner.scan [ source.Value ] |> Seq.length
