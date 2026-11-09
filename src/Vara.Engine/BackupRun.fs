namespace Vara.Engine

open System
open System.IO
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Vara.Domain

/// The effects of a backup run, assembled once by the composition root.
type BackupEnv =
    { Clock: unit -> DateTimeOffset
      Links: Links
      OpenRead: string -> Stream
      CheckpointInterval: TimeSpan
      /// True once a graceful stop was requested.
      ShouldStop: unit -> bool
      Observers: Observers
      /// Where a secondary failure goes when it must not replace the original one.
      Diagnostics: string -> unit }

/// The backup run and the dry-run plan: walk, read signatures, store, place, record, commit, in the order the spec fixes.
module BackupRun =
    let live (shouldStop: unit -> bool) : BackupEnv =
        { Clock = fun () -> DateTimeOffset.UtcNow
          Links = ContentStore.realLinks
          OpenRead = Signatures.openFile
          CheckpointInterval = TimeSpan.FromSeconds 30.0
          ShouldStop = shouldStop
          Observers = Observers.none
          Diagnostics = fun message -> Console.Error.WriteLine message }

    // The most common target is a slower external drive, which does best with one stream written at a time. The scan
    // stage only reads the source, so it uses every processor.
    let private defaultTransferConcurrency = 1

    let private scanConcurrency (profile: Profile) : int =
        match Profile.concurrency profile |> Option.bind (fun c -> c.Scan) with
        | Some limit -> ConcurrencyLimit.value limit
        | None -> Environment.ProcessorCount

    let private transferConcurrency (profile: Profile) : int =
        match Profile.concurrency profile |> Option.bind (fun c -> c.Transfer) with
        | Some limit -> ConcurrencyLimit.value limit
        | None -> defaultTransferConcurrency

    let private databasePath (profile: Profile) : string =
        SqliteManifest.pathIn (AbsolutePath.value (Profile.target profile))

    /// How many scanned entries pass between two reports of the running count. A display redraws far less often than
    /// the scan yields entries, so reporting each one would only allocate; a hundred keeps the count moving visibly.
    let scanReportInterval = 100

    /// The scan, counting its entries for the display when there is one. Without a display the scan is returned as it
    /// is, so the count costs nothing.
    let private counted (observers: Observers) (scan: seq<ScanEntry>) : seq<ScanEntry> =
        match observers.ScanProgress with
        | ValueNone -> scan
        | ValueSome report ->
            let seen = ref 0

            scan
            |> Seq.map (fun entry ->
                seen.Value <- seen.Value + 1

                if seen.Value % scanReportInterval = 0 then
                    report seen.Value

                entry)

    /// Scan, diff and plan, the sequence a real run and a dry run share. Returns the plan and the scan's failures.
    let private planFor (env: BackupEnv) (profile: Profile) (tracked: TrackedPaths) : struct (BackupPlan * ScanFailure list) =
        let diff = Diff.run tracked (counted env.Observers (Scanner.scan (Profile.sources profile)))
        let candidates = Planning.moveCandidates diff.Deleted
        let adds = Planning.sizeMatchedAdds candidates diff.Pending
        let hashes = Signatures.readFullHashes env.OpenRead (scanConcurrency profile) candidates adds
        struct (Planning.plan tracked diff candidates hashes, diff.Failures)

    /// Records a run's failure and commits it, so that history shows the failed run and not a snapshot left `Running`.
    /// A failure of the recording itself goes to the diagnostics: the original failure is the one the caller gets.
    let private recordFailure (env: BackupEnv) (snapshotId: int64) (batch: ManifestBatch) : unit =
        try
            SqliteManifest.finishSnapshot batch snapshotId SnapshotStatus.Failed (env.Clock()) (Run.initial (env.Clock())).Counts
            SqliteManifest.checkpoint batch |> SqliteManifest.discard
        with recording ->
            env.Diagnostics $"Failed to record snapshot {snapshotId}'s failure state: {recording}"

    /// A real backup of the profile, or the reason it was refused. A failure other than a refusal is an exception: it is
    /// recorded as a failed snapshot first, then rethrown with its original stack.
    let run (env: BackupEnv) (profile: Profile) : Task<Result<RunResult, BackupRefusal>> =
        task {
            match RunLock.tryAcquire (Profile.name profile) (Profile.target profile) with
            | Error refusal -> return Error refusal
            | Ok lockHandle ->
                use _lock = lockHandle
                let store = ContentStore.layout (AbsolutePath.value (Profile.target profile))
                ContentStore.ensureDirectories store
                let manifest = SqliteManifest.openAt (databasePath profile) true

                try
                    SqliteManifest.reconcileIncompleteSnapshots manifest (env.Clock())
                    let supportsHardlinks = ContentStore.probeHardlinkSupport env.Links store
                    ContentStore.cleanupOrphanedTemp store
                    let startedAt = env.Clock()

                    // Begun outside the batch, so an interrupted run stays discoverable as `Running`. Everything after
                    // is grouped into checkpoint commits.
                    let snapshotId = SqliteManifest.beginSnapshot manifest startedAt
                    let batch = SqliteManifest.beginBatch manifest

                    let executionEnv: ExecutionEnv =
                        { Store = store
                          Links = env.Links
                          SupportsHardlinks = supportsHardlinks
                          OpenRead = env.OpenRead
                          Now = env.Clock
                          CheckpointInterval = env.CheckpointInterval
                          TransferConcurrency = transferConcurrency profile
                          Observers = env.Observers
                          ShouldStop = env.ShouldStop }

                    let! attempted =
                        Flow.attempt (
                            task {
                                let struct (plan, scanFailures) = planFor env profile (SqliteManifest.currentState manifest)
                                let! ended = Execution.run executionEnv snapshotId startedAt batch plan
                                return struct (ended, scanFailures)
                            }
                        )

                    match attempted with
                    | Error failure ->
                        // Planning failed before execution returned a batch; the initial one is still open.
                        recordFailure env snapshotId batch
                        return Flow.rethrow failure
                    | Ok(struct (ended, scanFailures)) ->
                        match ended.Failure with
                        | ValueSome failure ->
                            recordFailure env snapshotId ended.Batch
                            return Flow.rethrow failure
                        | ValueNone ->
                            let result = Run.finish (env.ShouldStop()) scanFailures ended.Run
                            SqliteManifest.finishSnapshot ended.Batch snapshotId result.Status (env.Clock()) result.Totals
                            SqliteManifest.checkpoint ended.Batch |> SqliteManifest.discard
                            return Ok result
                finally
                    SqliteManifest.close manifest
        }

    /// What a real run would do, without writing anything: no content, no rows, no snapshot, and no manifest file
    /// created. Takes the lock like a real run, so the view is consistent and cannot race a starting run.
    let dryRun (env: BackupEnv) (profile: Profile) : Result<PlanSummary, BackupRefusal> =
        match RunLock.tryAcquire (Profile.name profile) (Profile.target profile) with
        | Error refusal -> Error refusal
        | Ok lockHandle ->
            use _lock = lockHandle
            let manifest = SqliteManifest.openAt (databasePath profile) false

            try
                let struct (plan, scanFailures) = planFor env profile (SqliteManifest.currentState manifest)
                Ok(Run.summarizePlan scanFailures plan)
            finally
                SqliteManifest.close manifest
