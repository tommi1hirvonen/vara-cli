/// Fixture helpers shared by the scenarios: scratch directories and deterministic content.
module Vara.Benchmarks.BenchmarkFiles

open System
open System.IO
open Vara.Domain

let createScratchDirectory () : string =
    let path = Path.Combine(Path.GetTempPath(), "vara-benchmarks", $"{Environment.ProcessId}-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore
    path

let createDeterministicBytes (size: int) (salt: int) : byte[] =
    Array.init size (fun i -> byte ((i + salt) % 251))

/// Deletes a directory tree, clearing the read-only attribute that mirror and blob files carry.
let deleteDirectory (path: string) : unit =
    if Directory.Exists path then
        for file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) do
            File.SetAttributes(file, FileAttributes.Normal)

        Directory.Delete(path, true)

let absolute (path: string) : AbsolutePath = (AbsolutePath.tryCreate path).Value

/// A recursive source with no filters.
let sourceOf (root: string) : Source =
    { Path = absolute root
      Recursive = true
      Excludes = []
      IncludeGlobs = []
      ExcludeGlobs = [] }
