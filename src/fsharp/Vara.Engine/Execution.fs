namespace Vara.Engine

open System
open System.IO
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Vara.Domain

/// What a progress display may observe while a plan runs. Every function runs on the single recorder, one call at
/// a time. `BytesCopied` is optional because chunk events cost channel traffic nobody pays for without a display.
type Observers =
    { TransferPhaseStarting: unit -> unit
      BytesCopied: (int64 -> unit) voption
      FileStarted: string -> unit
      FileFinished: string -> unit
      FileTransferred: unit -> unit }

module Observers =
    let none: Observers =
        { TransferPhaseStarting = ignore
          BytesCopied = ValueNone
          FileStarted = ignore
          FileFinished = ignore
          FileTransferred = ignore }

/// The effects a plan runs against, passed in once by the composition root.
type ExecutionEnv =
    { Store: StoreLayout
      Links: Links
      SupportsHardlinks: bool
      OpenRead: string -> Stream
      Now: unit -> DateTimeOffset
      /// A checkpoint is committed once this much time has passed since the previous one.
      CheckpointInterval: TimeSpan
      TransferConcurrency: int
      Observers: Observers
      /// True once a graceful stop was requested: no further operation starts, those running finish.
      ShouldStop: unit -> bool }

/// How execution ended. `Batch` is the run's latest write transaction, which the caller needs to record how the
/// snapshot ended even when `Failure` is set. `Failure` is the first unexpected failure, kept as a value so that
/// it can be recorded and then rethrown with its original stack.
type ExecutionEnd =
    { Run: RunState
      Batch: ManifestBatch
      Failure: ExceptionDispatchInfo voption }

/// What pool workers publish. The recorder turns each into an observer call, a manifest write or a state change.
/// A struct, so that publishing costs no allocation of its own; only the fields of the case in use are meaningful.
[<Struct>]
type private TransferEvent =
    | Started of started: string
    | Bytes of count: int64
    | Finished of finished: string
    | Completed of completion: Completion
    | Crashed of crash: exn

/// Carries out a plan: metadata operations in order, then transfers in a bounded pool feeding one recorder that
/// owns the manifest transaction, the run state and the checkpoint clock.
module Execution =
    let private isIoFailure (ex: exn) : bool =
        match ex with
        | :? IOException
        | :? UnauthorizedAccessException -> true
        | _ -> false

    /// The recorder's state between events.
    [<Struct>]
    type private Recorder =
        { Run: RunState
          Batch: ManifestBatch
          Crashes: exn list
          Failure: ExceptionDispatchInfo voption }

    /// Applies a completed operation: the new state, its rows written in the batch, and a checkpoint when one is due.
    /// A failure of the manifest itself is captured, not thrown, so that the latest batch is not lost.
    let private record (env: ExecutionEnv) (snapshotId: int64) (recordedAt: DateTimeOffset) (recorder: Recorder) (completion: Completion) : Recorder =
        if recorder.Failure.IsSome then
            recorder
        else
            try
                let struct (run, rows) = Run.apply recordedAt recorder.Run completion

                for row in rows do
                    SqliteManifest.record recorder.Batch snapshotId row

                let now = env.Now()

                if Run.checkpointDue env.CheckpointInterval now run then
                    { recorder with
                        Run = Run.checkpointed now run
                        Batch = SqliteManifest.checkpoint recorder.Batch }
                else
                    { recorder with Run = run }
            with error ->
                { recorder with Failure = ValueSome(ExceptionDispatchInfo.Capture error) }

    let private failedPath (operation: MetadataOperation) : MirrorPath =
        match operation with
        | MetadataOperation.Move move -> move.MoveTo
        | MetadataOperation.Delete delete -> delete.DeletePath
        | MetadataOperation.Link link -> link.LinkAt

    /// Performs the mirror side of a metadata operation. An I/O failure becomes a failed completion; anything else
    /// propagates.
    let private performMetadata (env: ExecutionEnv) (operation: MetadataOperation) : Completion =
        try
            match operation with
            | MetadataOperation.Move move ->
                ContentStore.moveMirrorEntry env.Store move.MoveFrom move.MoveTo
                Completion.Moved move
            | MetadataOperation.Delete delete ->
                // A link has no blob to remove.
                match delete.Deleted with
                | TrackedState.Regular content -> ContentStore.removeFromMirror env.Store delete.DeletePath content.Hash
                | TrackedState.Linked _ -> ()

                Completion.Deleted delete
            | MetadataOperation.Link link ->
                // A file turned into a link must lose its mirror copy.
                match link.Superseded with
                | ValueSome hash -> ContentStore.removeFromMirror env.Store link.LinkAt hash
                | ValueNone -> ()

                Completion.Linked link
        with ex when isIoFailure ex ->
            Completion.Failed(failedPath operation)

    let rec private runMetadata
        (env: ExecutionEnv)
        (snapshotId: int64)
        (recordedAt: DateTimeOffset)
        (recorder: Recorder)
        (operations: MetadataOperation list)
        : Recorder =
        match operations with
        | [] -> recorder
        | operation :: rest ->
            // Once a stop is requested nothing new starts; the sequential loop has nothing else in flight.
            if env.ShouldStop() || recorder.Failure.IsSome then
                recorder
            else
                let completion =
                    try
                        Ok(performMetadata env operation)
                    with error ->
                        Error(ExceptionDispatchInfo.Capture error)

                match completion with
                | Ok completion -> runMetadata env snapshotId recordedAt (record env snapshotId recordedAt recorder completion) rest
                | Error failure -> { recorder with Failure = ValueSome failure }

    /// Streams the source into the store while hashing it, then places it. Raises on I/O failure. A source whose size
    /// or modification time changed since the scan is a failed operation: its content is not placed and no row is
    /// written, so the next run selects it again.
    let private transferContent (env: ExecutionEnv) (emit: TransferEvent -> unit) (transfer: Transfer) : Completion =
        let file = transfer.Source
        let sourcePath = SourcePath.value file.FileSource

        let kind, previous =
            match transfer.TransferKind with
            | TransferKind.Added -> ChangeKind.Added, ValueNone
            | TransferKind.Changed superseded -> ChangeKind.Changed, superseded

        // Without hard links a placement copies the content again; the two passes share the file's progress,
        // half each, so the total reported never exceeds the planned bytes.
        let reported = ref 0L

        let storeCallback, mirrorCallback =
            match env.Observers.BytesCopied with
            | ValueNone -> ValueNone, ValueNone
            | ValueSome _ when env.SupportsHardlinks ->
                ValueSome(fun (count: int64) -> emit (Bytes count)), ValueNone
            | ValueSome _ ->
                let stored = ref 0L
                let mirrored = ref 0L

                let advance (target: int64) =
                    let delta = target - reported.Value

                    if delta > 0L then
                        reported.Value <- reported.Value + delta
                        emit (Bytes delta)

                ValueSome(fun (chunk: int64) ->
                    stored.Value <- stored.Value + chunk
                    advance (stored.Value / 2L)),
                ValueSome(fun (chunk: int64) ->
                    mirrored.Value <- mirrored.Value + chunk
                    advance (file.Size / 2L + mirrored.Value / 2L))

        let struct (hash, size, quick) =
            use source = env.OpenRead sourcePath
            let prefix = Signatures.readPrefix source
            let quick = Signatures.quickOf prefix
            use replay = new PrefixReplayStream(prefix, source)
            let struct (hash, size) = ContentStore.storeFromStream env.Store replay storeCallback
            struct (hash, size, quick)

        let after = FileInfo sourcePath

        if after.Length <> file.Size || not ((DateTimeOffset after.LastWriteTimeUtc).Equals file.ModifiedAt) then
            Completion.Failed file.FilePath
        else
            ContentStore.placeAtMirrorPath env.Links env.Store env.SupportsHardlinks hash file.FilePath mirrorCallback previous

            if not env.SupportsHardlinks && env.Observers.BytesCopied.IsSome then
                let remainder = size - reported.Value

                if remainder > 0L then
                    emit (Bytes remainder)

            Completion.Transferred
                { Path = file.FilePath
                  Content =
                    { Hash = hash
                      Size = size
                      SourceModifiedAt = file.ModifiedAt
                      Quick = ValueSome quick }
                  Kind = kind }

    /// One pool worker's item. Never throws: an I/O failure is a failed completion, anything else a crash event,
    /// so that nothing escapes the pool and the recorder drains everything produced before the crash.
    let private runTransfer (env: ExecutionEnv) (emit: TransferEvent -> unit) (transfer: Transfer) : unit =
        let sourcePath = SourcePath.value transfer.Source.FileSource
        emit (Started sourcePath)

        let outcome =
            try
                Completed(transferContent env emit transfer)
            with
            | ex when isIoFailure ex -> Completed(Completion.Failed transfer.Source.FilePath)
            | ex -> Crashed ex

        // The file leaves the active list once its I/O is done, not once its row is written.
        emit (Finished sourcePath)
        emit outcome

    let private onEvent
        (env: ExecutionEnv)
        (snapshotId: int64)
        (recordedAt: DateTimeOffset)
        (stop: CancellationTokenSource)
        (recorder: Recorder)
        (event: TransferEvent)
        : Recorder =
        match event with
        | Started path ->
            env.Observers.FileStarted path
            recorder
        | Bytes count ->
            match env.Observers.BytesCopied with
            | ValueSome report -> report count
            | ValueNone -> ()

            recorder
        | Finished path ->
            env.Observers.FileFinished path
            recorder
        | Completed completion ->
            let next = record env snapshotId recordedAt recorder completion

            match completion with
            | Completion.Transferred _ when next.Failure.IsNone -> env.Observers.FileTransferred()
            | _ -> ()

            if next.Failure.IsSome then
                stop.Cancel()

            next
        | Crashed error ->
            stop.Cancel()
            { recorder with Crashes = error :: recorder.Crashes }

    /// Runs the plan's operations and records each as it completes. Failures other than per-operation I/O errors do
    /// not throw: they come back in `Failure`, after the pool has stopped and everything produced before them has been
    /// recorded, so the caller can record the snapshot's end and then rethrow.
    let run
        (env: ExecutionEnv)
        (snapshotId: int64)
        (recordedAt: DateTimeOffset)
        (batch: ManifestBatch)
        (plan: BackupPlan)
        : Task<ExecutionEnd> =
        task {
            let initial =
                { Run = Run.initial (env.Now())
                  Batch = batch
                  Crashes = []
                  Failure = ValueNone }

            let afterMetadata = runMetadata env snapshotId recordedAt initial plan.Metadata

            // Fired once, even when there is nothing to transfer, so a throughput clock starts only when bytes begin
            // to move and excludes the metadata pass.
            env.Observers.TransferPhaseStarting()

            use stop = new CancellationTokenSource()

            let struct (events, pool) =
                Flow.parallelEmit
                    env.TransferConcurrency
                    (fun () -> env.ShouldStop() || stop.IsCancellationRequested || afterMetadata.Failure.IsSome)
                    (fun emit transfer -> runTransfer env emit transfer)
                    plan.Transfers

            let! folding = Flow.attempt (Flow.foldChannel (onEvent env snapshotId recordedAt stop) afterMetadata events)

            // Wait for the pool before the caller records an ending, so no worker still writes to the store. The pool
            // never throws, so awaiting it cannot mask a failure of the fold.
            stop.Cancel()
            do! pool

            let folded =
                match folding with
                | Ok folded -> folded
                | Error failure -> Flow.rethrow failure

            let failure =
                match folded.Failure with
                | ValueSome failure -> ValueSome failure
                | ValueNone ->
                    match folded.Crashes with
                    | [] -> ValueNone
                    | crashes ->
                        ValueSome(ExceptionDispatchInfo.Capture(AggregateException(List.rev crashes)))

            return
                { Run = folded.Run
                  Batch = folded.Batch
                  Failure = failure }
        }
