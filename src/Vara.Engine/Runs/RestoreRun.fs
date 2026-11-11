namespace Vara.Engine

open System
open System.IO
open Vara.Domain

/// The effects a restore performs, as functions. A test passes one that raises in place of a test hook.
type RestoreEnv =
    { /// Opens the stored content of a blob for reading.
      OpenBlob: ContentHash -> Stream
      /// Whether a graceful stop was requested; checked between files, never inside one.
      ShouldStop: unit -> bool
      /// Receives the progress of the restore: the files that start and finish, and the bytes copied.
      Publish: ProgressEvent -> unit }

/// A file the restore could not write or remove, and why.
type RestoreFailure = { Destination: string; Reason: string }

/// How a restore ended. A per-file I/O failure is a value the restore records and goes on past.
type RestoreResult =
    { Written: int
      Removed: int
      /// The destinations that could not be written or removed, in the order they were attempted.
      Failed: RestoreFailure list
      /// A stop was requested before the last planned path was applied. What was applied stays applied.
      Stopped: bool }

/// The stored content of a file does not hash to the value recorded for the version being restored: the blob is damaged.
/// An `IOException` so that the restore records it as a per-file failure and goes on.
type RestoredContentMismatchException(expected: ContentHash, actual: ContentHash) =
    inherit
        IOException
            $"The stored content does not match what was recorded for this file (recorded {ContentHash.value expected}, read {ContentHash.value actual}). Run 'vara check' to find damaged content."

    member _.Expected = expected
    member _.Actual = actual

/// Carries out a `RestorePlan`: the removals, then the writes, each write staged beside its destination and moved into
/// place, so that a destination is either the restored content or exactly what it was.
module RestoreRun =
    open FileSupport

    let private bufferSize = 81_920

    /// The temporary name beside the destination that the content is staged under.
    let private stagingNameFor (destination: string) : string =
        let directory = Path.GetDirectoryName destination |> nonNull
        Path.Combine(directory, $".vara-restore-{Guid.NewGuid():N}.tmp")

    /// Writes one file: copy to a staging name while hashing the bytes, check the hash against the recorded one, flush
    /// it to disk, stamp the modification time, and move it over the destination. The content is verified before it is
    /// moved into place. A failure at any step, a hash mismatch included, removes the staging file and leaves the
    /// destination as it was.
    let private writeFile (env: RestoreEnv) (write: RestoreWrite) : unit =
        Directory.CreateDirectory(Path.GetDirectoryName write.Destination |> nonNull) |> ignore
        let staging = stagingNameFor write.Destination

        try
            use source = new BufferedStream(env.OpenBlob write.Source, bufferSize)

            do
                use target = new FileStream(staging, FileMode.CreateNew, FileAccess.Write)
                use sink = new BufferedStream(target, bufferSize)
                use tee = new TeeStream(source, sink, ValueSome(fun delta -> env.Publish(ProgressEvent.BytesCopied delta)))
                let actual = Hashing.hashStreamReporting ignore tee

                if actual <> write.Source then
                    raise (RestoredContentMismatchException(write.Source, actual))

                sink.Flush()
                target.Flush true

            File.SetLastWriteTimeUtc(staging, write.ModifiedAt.UtcDateTime)
            // Move(overwrite) throws on a read-only destination, so the attribute is cleared first.
            clearReadOnly write.Destination
            File.Move(staging, write.Destination, true)
        with _ ->
            tryDelete staging
            reraise ()

    /// What the restore has done so far. Failures are newest first.
    type private Done =
        { Written: int
          Removed: int
          Failures: RestoreFailure list }

    /// Runs the action; an I/O failure is a value that names the destination and why, not an exception.
    let private attempt (destination: string) (action: unit -> unit) : Result<unit, RestoreFailure> =
        try
            Ok(action ())
        with IoFailure error ->
            Error { Destination = destination; Reason = error.Message }

    /// Applies the step to each item in order, stopping before the next item once a stop has been requested. The
    /// second value says whether it stopped early. The step is never interrupted: the item in progress finishes.
    [<TailCall>]
    let rec private applyAll (env: RestoreEnv) (step: 'T -> Done -> Done) (items: 'T list) (progress: Done) : struct (Done * bool) =
        match items with
        | [] -> struct (progress, false)
        | item :: rest ->
            if env.ShouldStop() then struct (progress, true) else applyAll env step rest (step item progress)

    let private removeStep (destination: string) (progress: Done) : Done =
        let removal () =
            if File.Exists destination then
                clearReadOnly destination
                File.Delete destination

        match attempt destination removal with
        | Ok() -> { progress with Removed = progress.Removed + 1 }
        | Error failure -> { progress with Failures = failure :: progress.Failures }

    let private writeStep (env: RestoreEnv) (write: RestoreWrite) (progress: Done) : Done =
        env.Publish(ProgressEvent.FileStarted write.Destination)

        // An error that is not a per-file failure is not caught by `attempt`: it leaves with the stack it was raised
        // with, once the file in progress is closed in the display.
        try
            match attempt write.Destination (fun () -> writeFile env write) with
            | Ok() ->
                env.Publish ProgressEvent.FileTransferred
                { progress with Written = progress.Written + 1 }
            | Error failure -> { progress with Failures = failure :: progress.Failures }
        finally
            env.Publish(ProgressEvent.FileFinished write.Destination)

    /// Applies the plan: the removals, then the writes. A write or removal that fails on an I/O error is recorded and
    /// the restore continues; any other exception ends it, after the staging file is removed, rethrown with its
    /// original stack. A requested stop is honoured between paths, and what was applied stays applied.
    let execute (env: RestoreEnv) (plan: RestorePlan) : RestoreResult =
        let start = { Written = 0; Removed = 0; Failures = [] }
        let struct (afterRemovals, stoppedInRemovals) = applyAll env removeStep plan.Removals start

        let struct (finished, stopped) =
            if stoppedInRemovals then
                struct (afterRemovals, true)
            else
                applyAll env (writeStep env) plan.Writes afterRemovals

        { Written = finished.Written
          Removed = finished.Removed
          Failed = List.rev finished.Failures
          Stopped = stopped }

    /// Checks every destination of the plan against the live mirror. Each is resolved to its real path first, through
    /// every junction and symlink on the way, and the pure guard decides. Nothing is written by this check.
    let checkDestinations (store: StoreLayout) (plan: RestorePlan) : Result<unit, HistoryError> =
        let mirror = Kernel32.resolveRealPath store.MirrorRoot

        Restore.destinations plan
        |> List.fold
            (fun outcome destination ->
                outcome |> Result.bind (fun () -> Restore.guard mirror (Kernel32.resolveRealPath destination)))
            (Ok())

    /// Whether the single destination of a file restore is in the mirror, for the check before anything else.
    let checkDestination (store: StoreLayout) (destination: string) : Result<unit, HistoryError> =
        Restore.guard (Kernel32.resolveRealPath store.MirrorRoot) (Kernel32.resolveRealPath destination)
