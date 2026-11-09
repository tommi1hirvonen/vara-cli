module Vara.Benchmarks.Program

open BenchmarkDotNet.Running

[<EntryPoint>]
let main (argv: string[]) : int =
    BenchmarkSwitcher.FromAssembly(typeof<DirectoryScanBenchmarks>.Assembly).Run argv |> ignore
    0
