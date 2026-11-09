namespace Vara.Benchmarks

open System.IO
open BenchmarkDotNet.Attributes
open Vara.Domain
open Vara.Engine

/// The buffered source-to-temp-file tee-and-hash path across buffer sizes, including the current 1 MiB setting. It uses
/// prepared source files and measures the real file read, write and hash work.
[<MemoryDiagnoser>]
type ContentStoreBufferBenchmarks() =
    let mutable scratchDirectory = ""
    let mutable sourcePath = ""
    let mutable destinationPath = ""

    [<Params(65536, 1048576)>]
    member val ContentSize = 0 with get, set

    [<Params(4096, 16384, 65536, 81920, 262144, 1048576)>]
    member val BufferSize = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() : unit =
        scratchDirectory <- BenchmarkFiles.createScratchDirectory ()
        sourcePath <- Path.Combine(scratchDirectory, "source.bin")
        destinationPath <- Path.Combine(scratchDirectory, "stored.bin")
        File.WriteAllBytes(sourcePath, BenchmarkFiles.createDeterministicBytes this.ContentSize 23)

    [<GlobalCleanup>]
    member _.Cleanup() : unit = BenchmarkFiles.deleteDirectory scratchDirectory

    [<Benchmark>]
    member this.HashWhileCopyingToBufferedFile() : string =
        use source = File.OpenRead sourcePath
        use destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write)
        use bufferedSource = new BufferedStream(source, this.BufferSize)
        use bufferedDestination = new BufferedStream(destination, this.BufferSize)
        use tee = new TeeStream(bufferedSource, bufferedDestination, ValueNone)
        ContentHash.value (Hashing.hashStream tee)
