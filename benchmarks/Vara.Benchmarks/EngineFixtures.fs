/// The engine fixtures of the executor and pipeline scenarios. Setup and cleanup live here too, so that the timed calls
/// are exactly the engine's own work.
module Vara.Benchmarks.EngineFixtures

open System
open System.IO
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

/// A prepared executor run: an open manifest with its first snapshot and batch, and an all-added plan.
type ExecutorFixture =
    { Env: ExecutionEnv
      Manifest: ManifestHandle
      SnapshotId: int64
      RecordedAt: DateTimeOffset
      Batch: ManifestBatch
      Plan: BackupPlan }

let prepareExecutor (sourceRoot: string) (targetRoot: string) (fileCount: int) (transferConcurrency: int) : ExecutorFixture =
    let store = ContentStore.layout targetRoot
    ContentStore.ensureDirectories store
    let supportsHardlinks = ContentStore.probeHardlinkSupport ContentStore.realLinks store
    let manifest = SqliteManifest.openAt (Path.Combine(targetRoot, ".vara", "profile.db")) true
    let recordedAt = DateTimeOffset.UtcNow
    let snapshotId = SqliteManifest.beginSnapshot manifest recordedAt
    let batch = SqliteManifest.beginBatch manifest

    let transfers =
        [ for i in 0 .. fileCount - 1 do
              let path = Path.Combine(sourceRoot, $"file-{i:D4}.bin")
              let info = FileInfo path

              { Source =
                  { FilePath = MirrorPath.ofString $"source\file-{i:D4}.bin"
                    FileSource = SourcePath.ofString path
                    Size = info.Length
                    ModifiedAt = DateTimeOffset info.LastWriteTimeUtc }
                TransferKind = TransferKind.Added } ]

    { Env =
        { Store = store
          Links = ContentStore.realLinks
          SupportsHardlinks = supportsHardlinks
          OpenRead = Signatures.openFile
          Now = fun () -> DateTimeOffset.UtcNow
          CheckpointInterval = TimeSpan.FromSeconds 30.0
          TransferConcurrency = transferConcurrency
          Observers = Observers.none
          ShouldStop = fun () -> false }
      Manifest = manifest
      SnapshotId = snapshotId
      RecordedAt = recordedAt
      Batch = batch
      Plan =
        { Metadata = []
          Transfers = transfers
          TotalBytes = transfers |> List.sumBy (fun transfer -> transfer.Source.Size) } }

let execute (fixture: ExecutorFixture) : Task<ExecutionEnd> =
    Execution.run fixture.Env fixture.SnapshotId fixture.RecordedAt fixture.Batch fixture.Plan

/// Commits what the run wrote and closes the manifest. `ended` is the run's result, when it produced one.
let closeExecutor (fixture: ExecutorFixture) (ended: ExecutionEnd voption) : unit =
    match ended with
    | ValueSome ended -> SqliteManifest.checkpoint ended.Batch |> SqliteManifest.discard
    | ValueNone -> SqliteManifest.discard fixture.Batch

    SqliteManifest.close fixture.Manifest

/// The profile of a pipeline run: one recursive source and the target, with the engine's default concurrency.
let profileOf (sourceRoot: string) (targetRoot: string) : Profile =
    match Profile.create "benchmark" (BenchmarkFiles.absolute targetRoot) [ BenchmarkFiles.sourceOf sourceRoot ] None None with
    | Ok profile -> profile
    | Error _ -> failwith "invalid benchmark profile"

/// A first backup of the profile. Returns the files added, so the caller can check the run did its work.
let runFirstBackup (profile: Profile) : Task<int> =
    task {
        match! BackupRun.run (BackupRun.live (fun () -> false)) profile with
        | Ok result -> return result.Totals.FilesAdded
        | Error _ -> return failwith "backup refused"
    }
