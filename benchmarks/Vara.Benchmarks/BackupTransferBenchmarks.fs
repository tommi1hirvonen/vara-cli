namespace Vara.Benchmarks

open System.IO
open System.Threading.Tasks
open BenchmarkDotNet.Attributes
open Microsoft.Data.Sqlite
open Vara.Domain
open Vara.Engine
open Vara.Benchmarks.EngineFixtures

/// The source fixture and the reset of the target that the executor and the pipeline scenarios share. The target and the
/// manifest are reset during iteration setup, outside the timed method, so these scenarios are repeated clean first runs.
type BackupBenchmarkBase() =
    member val ScratchDirectory = "" with get, set
    member val SourceRoot = "" with get, set
    member val TargetRoot = "" with get, set

    [<Params(8, 64)>]
    member val FileCount = 0 with get, set

    [<Params(65536, 1048576)>]
    member val BytesPerFile = 0 with get, set

    member this.CreateSourceFixture() : unit =
        this.ScratchDirectory <- BenchmarkFiles.createScratchDirectory ()
        this.SourceRoot <- Path.Combine(this.ScratchDirectory, "source")
        this.TargetRoot <- Path.Combine(this.ScratchDirectory, "target")
        Directory.CreateDirectory this.SourceRoot |> ignore

        for i in 0 .. this.FileCount - 1 do
            File.WriteAllBytes(Path.Combine(this.SourceRoot, $"file-{i:D4}.bin"), BenchmarkFiles.createDeterministicBytes this.BytesPerFile i)

    member this.ResetTarget() : unit =
        SqliteConnection.ClearAllPools()
        BenchmarkFiles.deleteDirectory this.TargetRoot
        Directory.CreateDirectory this.TargetRoot |> ignore

    member this.CleanupWorkspace() : unit =
        SqliteConnection.ClearAllPools()
        BenchmarkFiles.deleteDirectory this.ScratchDirectory

/// Source reads, hashing, content-store writes, mirror placement and manifest writes for a prepared all-added plan.
[<MemoryDiagnoser>]
type BackupExecutorBenchmarks() =
    inherit BackupBenchmarkBase()
    let mutable fixture: ExecutorFixture option = None

    [<Params(1, 4)>]
    member val TransferConcurrency = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() : unit = this.CreateSourceFixture()

    member private _.CloseIteration() : unit =
        match fixture with
        | Some current ->
            closeExecutor current ValueNone
            fixture <- None
        | None -> ()

    [<IterationSetup>]
    member this.SetupIteration() : unit =
        this.CloseIteration()
        this.ResetTarget()
        fixture <- Some(prepareExecutor this.SourceRoot this.TargetRoot this.FileCount this.TransferConcurrency)

    [<IterationCleanup>]
    member this.CleanupIteration() : unit = this.CloseIteration()

    [<GlobalCleanup>]
    member this.Cleanup() : unit =
        this.CloseIteration()
        this.CleanupWorkspace()

    [<Benchmark>]
    member _.ExecuteTransfers() : Task<ExecutionEnd> = execute fixture.Value

/// A first backup of an all-added source tree with the real scanner, planner, executor, content store, run lock and SQLite
/// manifest.
[<MemoryDiagnoser>]
type BackupPipelineBenchmarks() =
    inherit BackupBenchmarkBase()
    let mutable profile: Profile option = None

    [<GlobalSetup>]
    member this.Setup() : unit =
        this.CreateSourceFixture()
        profile <- Some(profileOf this.SourceRoot this.TargetRoot)

    [<IterationSetup>]
    member this.SetupIteration() : unit = this.ResetTarget()

    [<GlobalCleanup>]
    member this.Cleanup() : unit = this.CleanupWorkspace()

    [<Benchmark>]
    member _.RunFirstBackup() : Task<int> = runFirstBackup profile.Value

/// The planning pass of a dry run over a source with many small files and no manifest, with and without the per-path
/// changes a change tree needs. Without them the pass keeps nothing beyond the counts; with them it also maps every
/// planned change to an entry before the lock is released.
[<MemoryDiagnoser>]
type BackupDryRunBenchmarks() =
    let mutable scratchDirectory = ""
    let mutable profile: Profile option = None

    [<Params(1000, 20000)>]
    member val FileCount = 0 with get, set

    [<Params(false, true)>]
    member val Detail = false with get, set

    [<GlobalSetup>]
    member this.Setup() : unit =
        scratchDirectory <- BenchmarkFiles.createScratchDirectory ()
        let sourceRoot = Path.Combine(scratchDirectory, "source")
        let targetRoot = Path.Combine(scratchDirectory, "target")
        Directory.CreateDirectory targetRoot |> ignore

        for i in 0 .. this.FileCount - 1 do
            let directory = Path.Combine(sourceRoot, $"folder-{i % 50:D2}", $"sub-{i % 7}")
            Directory.CreateDirectory directory |> ignore
            File.WriteAllBytes(Path.Combine(directory, $"file-{i:D6}.bin"), BenchmarkFiles.createDeterministicBytes 16 i)

        profile <- Some(profileOf sourceRoot targetRoot)

    [<GlobalCleanup>]
    member _.Cleanup() : unit =
        SqliteConnection.ClearAllPools()
        BenchmarkFiles.deleteDirectory scratchDirectory

    /// Returns the planned adds plus the entries returned, so the caller can check the pass did its work.
    [<Benchmark>]
    member this.PlanDryRun() : int =
        match BackupRun.dryRunDetailed (BackupRun.live (fun () -> false)) profile.Value this.Detail with
        | Ok(struct (summary, entries)) -> summary.Added + entries.Length
        | Error _ -> failwith "dry run refused"
