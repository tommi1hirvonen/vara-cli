namespace Vara.Engine

open System.IO
open Vara.Domain

/// The effects a prune performs, as functions. A test passes one that raises in place of a test hook.
type PruneEnv =
    { /// Deletes one blob from the store.
      DeleteBlob: ContentHash -> unit
      /// Whether a graceful stop was requested; checked between blobs, never inside one.
      ShouldStop: unit -> bool
      /// Receives the progress of the garbage collection: the total once it is known, then one item per blob.
      Publish: ProgressEvent -> unit }

/// How a prune ended. A blob that could not be deleted because of an I/O error is a value the run records and goes on
/// past.
type PruneResult =
    { SnapshotsRemoved: int
      BlobsRemoved: int
      /// The blobs that could not be deleted, in the order they were attempted.
      Failed: BlobFailure list
      /// A stop was requested before the run finished. What was done stays done.
      Stopped: bool }

/// Carries out a prune: the snapshot rows go in one transaction, then the blobs that nothing references go one by one.
module PruneRun =
    /// The effects of a prune against a store.
    let live (store: StoreLayout) (shouldStop: unit -> bool) (publish: ProgressEvent -> unit) : PruneEnv =
        { DeleteBlob = ContentStore.deleteBlob store
          ShouldStop = shouldStop
          Publish = publish }

    /// An error that ends one blob but not the prune: the file could not be deleted.
    let private isPerBlobFailure (error: exn) : bool =
        match error with
        | :? IOException
        | :? System.UnauthorizedAccessException -> true
        | _ -> false

    /// What the garbage collection has done so far. Failures are newest first.
    type private Collected =
        { Removed: int
          Failures: BlobFailure list }

    /// Deletes each blob in order, stopping before the next one once a stop has been requested (the blob in progress
    /// finishes). The second value says whether it stopped early. Anything but an I/O failure leaves with the stack it
    /// was raised with.
    let rec private deleteAll
        (env: PruneEnv)
        (blobs: ContentHash list)
        (collected: Collected)
        : struct (Collected * bool) =
        match blobs with
        | [] -> struct (collected, false)
        | blob :: rest ->
            if env.ShouldStop() then
                struct (collected, true)
            else
                let next =
                    try
                        env.DeleteBlob blob
                        env.Publish ProgressEvent.FileTransferred
                        { collected with Removed = collected.Removed + 1 }
                    with error when isPerBlobFailure error ->
                        env.Publish ProgressEvent.FileTransferred

                        { collected with
                            Failures = { Blob = blob; Reason = error.Message } :: collected.Failures }

                deleteAll env rest next

    /// The prune of one profile. A stop requested before it starts skips it entirely: removing the snapshots is one
    /// atomic step, and the stop is read only before it, never inside it. After it, the stop is read between blobs.
    /// `confirmed` is the list of snapshots the user was shown, when there was a prompt.
    let execute
        (env: PruneEnv)
        (store: StoreLayout)
        (handle: ManifestHandle)
        (policy: RetentionPolicy)
        (confirmed: int64 list voption)
        : PruneResult =
        if env.ShouldStop() then
            { SnapshotsRemoved = 0
              BlobsRemoved = 0
              Failed = []
              Stopped = true }
        else
            let snapshots = SqliteManifest.snapshots handle
            let existing = snapshots |> List.map _.SnapshotId |> Set.ofList
            let decision = Retention.evaluate policy snapshots
            let ids = Prune.snapshotsToRemove decision.Eligible confirmed existing
            let snapshotsRemoved = SqliteManifest.removeSnapshots handle ids

            // The rows are gone first, so a crash from here on leaves unreferenced blobs and never a referenced blob
            // missing; the next prune finds them again.
            let blobs =
                Prune.blobsToRemove (ContentStore.storedHashes store) (SqliteManifest.referencedHashes handle)

            // With nothing to remove, no total is published, so no bar for zero items is drawn.
            if not blobs.IsEmpty then
                env.Publish(ProgressEvent.TransferStarting(0L, blobs.Length))

            let struct (collected, stopped) = deleteAll env blobs { Removed = 0; Failures = [] }

            { SnapshotsRemoved = snapshotsRemoved
              BlobsRemoved = collected.Removed
              Failed = List.rev collected.Failures
              Stopped = stopped }

    /// Takes the target's run lock, opens the manifest for writing and runs the prune. The lock is taken only here,
    /// after any confirmation, so that a backup can run while the user decides. A target with no manifest has
    /// nothing to prune and none is created.
    let run
        (env: PruneEnv)
        (profileName: string)
        (target: AbsolutePath)
        (policy: RetentionPolicy)
        (confirmed: int64 list voption)
        : Result<PruneResult, BackupRefusal> =
        RunLock.tryAcquire profileName target
        |> Result.map (fun held ->
            use _held = held
            let root = AbsolutePath.value target
            let handle = SqliteManifest.openAt (SqliteManifest.pathIn root) false

            try
                execute env (ContentStore.layout root) handle policy confirmed
            finally
                SqliteManifest.close handle)
