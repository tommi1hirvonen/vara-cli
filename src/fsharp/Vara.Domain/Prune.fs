namespace Vara.Domain

/// A blob that a prune could not delete because of an I/O error, and why. The run records it and goes on.
type BlobFailure = { Blob: ContentHash; Reason: string }

/// The plan of a prune, as pure set arithmetic (retention-pruning: "Prune command", "Unreferenced content garbage
/// collection"). The shell performs it under the run lock.
module Prune =
    /// The snapshots a run removes: those eligible now, narrowed to the ones the user confirmed when there was a
    /// prompt, and to those that still exist. A snapshot that became eligible after the prompt was shown is not in the
    /// confirmed list and stays; a confirmed one that is already gone is dropped without an error. Ascending by id.
    let snapshotsToRemove (eligible: int64 list) (confirmed: int64 list voption) (existing: Set<int64>) : int64 list =
        let allowed =
            match confirmed with
            | ValueSome ids -> Set.ofList ids
            | ValueNone -> Set.ofList eligible

        eligible
        |> List.filter (fun id -> allowed.Contains id && existing.Contains id)
        |> List.distinct
        |> List.sort

    /// The blobs in the store that no remaining row references, in a stable order (by name).
    let blobsToRemove (stored: Set<ContentHash>) (referenced: Set<ContentHash>) : ContentHash list =
        Set.difference stored referenced |> Set.toList
