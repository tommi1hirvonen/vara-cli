namespace Vara.Engine

open System.IO
open Vara.Domain

/// The effects a check performs, as functions.
type CheckEnv =
    { /// Opens the stored content of a blob for reading.
      OpenBlob: ContentHash -> Stream
      /// Whether a graceful stop was requested; checked between blobs, never inside one.
      ShouldStop: unit -> bool
      /// Receives the progress: the totals once they are known, the bytes read, the work credited for a missing blob,
      /// and one item per blob checked.
      Publish: ProgressEvent -> unit }

/// Reads every blob that the manifest references and gives each a verdict. It never writes: not the store, not the
/// manifest.
module CheckRun =
    /// The effects of a check against a store.
    let live (store: StoreLayout) (shouldStop: unit -> bool) (publish: ProgressEvent -> unit) : CheckEnv =
        { OpenBlob = ContentStore.openRead store
          ShouldStop = shouldStop
          Publish = publish }

    /// What the check sees of one blob, and how many bytes it read to see it. A blob that vanishes between the listing
    /// and the read is absent. Only a full check reads: the content goes through the hash in large chunks, each
    /// reported as bytes read.
    let private observe (env: CheckEnv) (quick: bool) (stored: Set<ContentHash>) (hash: ContentHash) : struct (Observation * int64) =
        if not (stored.Contains hash) then
            struct (Observation.Absent, 0L)
        elif quick then
            struct (Observation.Present ValueNone, 0L)
        else
            // The count is local to this blob's read, a cell because the reporting function closes over it.
            let read = ref 0L

            try
                use content = env.OpenBlob hash

                let recomputed =
                    Hashing.hashStreamReporting
                        (fun chunk ->
                            read.Value <- read.Value + chunk
                            env.Publish(ProgressEvent.BytesCopied chunk))
                        content

                struct (Observation.Present(ValueSome recomputed), read.Value)
            with
            | :? FileNotFoundException
            | :? DirectoryNotFoundException -> struct (Observation.Absent, 0L)

    /// Verifies the blobs in order, stopping before the next one once a stop has been requested. The second value says
    /// whether it stopped early.
    let rec private verifyAll
        (env: CheckEnv)
        (quick: bool)
        (stored: Set<ContentHash>)
        (pathsOf: ContentHash -> string list)
        (blobs: (ContentHash * int64) list)
        (result: CheckResult)
        : struct (CheckResult * bool) =
        match blobs with
        | [] -> struct (result, false)
        | (hash, size) :: rest ->
            if env.ShouldStop() then
                struct (result, true)
            else
                let struct (observed, read) = observe env quick stored hash
                let verdict = Check.verdict quick hash observed

                // Work done without a byte read is credited, so the percentage moves and the throughput does not: a
                // missing blob's whole size, and the part of a present blob that its recorded size says is there but
                // that was not read (a truncated blob). Every blob thus completes its share, and a full check reaches
                // 100% whatever it finds.
                let unread = max 0L (size - read)

                if not quick && unread > 0L then
                    env.Publish(ProgressEvent.WorkCredited unread)

                env.Publish ProgressEvent.FileTransferred
                verifyAll env quick stored pathsOf rest (Check.add pathsOf hash verdict result)

    /// The check of one target: the referenced blobs against the stored ones.
    let execute (env: CheckEnv) (store: StoreLayout) (handle: ManifestHandle) (quick: bool) : CheckResult =
        let referenced = SqliteManifest.referencedSizes handle
        let stored = ContentStore.storedHashes store

        // The sum saturates, as the C# build's does, instead of overflowing on absurd sizes.
        let totalBytes =
            referenced
            |> List.fold (fun total (_, size) -> if size > System.Int64.MaxValue - total then System.Int64.MaxValue else total + max 0L size) 0L

        env.Publish(ProgressEvent.TransferStarting(totalBytes, referenced.Length))

        let struct (verified, stopped) =
            verifyAll env quick stored (SqliteManifest.pathsForHash handle) referenced Check.empty

        let referencedSet = referenced |> List.map fst |> Set.ofList
        Check.finish (Set.difference stored referencedSet |> Set.toList) stopped verified
