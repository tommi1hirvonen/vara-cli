namespace Vara.Domain

open System
open System.Collections.Generic

/// Deleted regular files grouped by size: the only things an added file can be a move of. Within a size the
/// order is the diff's deleted-path order, which breaks ties between identical candidates.
type MoveCandidates = Dictionary<int64, (MirrorPath * FileContent) list>

/// Turns a diff into a plan. The shell reads file content between `sizeMatchedAdds` and `plan`, so that no
/// function here reads a file.
module Planning =
    let moveCandidates (deleted: (MirrorPath * TrackedState) list) : MoveCandidates =
        let grouped = MoveCandidates()

        // `deleted` is walked backwards so that consing keeps each size's candidates in deleted-path order.
        for path, state in List.rev deleted do
            match state with
            | TrackedState.Regular content ->
                match grouped.TryGetValue content.Size with
                | true, existing -> grouped[content.Size] <- (path, content) :: existing
                | false, _ -> grouped[content.Size] <- [ (path, content) ]
            | TrackedState.Linked _ -> ()

        grouped

    /// The added files that share a size with a deleted candidate. Only these need their content read.
    /// A changed file is an edit, never a move, and a link has no content.
    let sizeMatchedAdds (candidates: MoveCandidates) (pending: PendingChange list) : ScannedFile list =
        pending
        |> List.choose (fun change ->
            match change with
            | PendingChange.Add file when candidates.ContainsKey file.Size -> Some file
            | _ -> None)

    /// Whether the rest of the added file must be read after its bounded prefix was hashed.
    /// False only when every candidate has a usable recorded signature and none equals the prefix hash, which
    /// rules every candidate out. A candidate without a usable signature cannot be ruled out, so it forces the read.
    let needsFullHash (candidates: (MirrorPath * FileContent) list) (prefixHash: string) : bool =
        let rec check (remaining: (MirrorPath * FileContent) list) (everyUsable: bool) (anyEqual: bool) =
            match remaining with
            | [] -> not everyUsable || anyEqual
            | (_, content) :: rest ->
                match content.Quick with
                | ValueSome signature when QuickSignature.isUsable signature ->
                    check rest everyUsable (anyEqual || String.Equals(signature.QuickHash, prefixHash, StringComparison.Ordinal))
                | _ -> check rest false anyEqual

        check candidates true false

    let private supersededHash (state: TrackedState voption) : ContentHash voption =
        match state with
        | ValueSome(TrackedState.Regular content) -> ValueSome content.Hash
        | ValueSome(TrackedState.Linked _)
        | ValueNone -> ValueNone

    let private lookup (tracked: TrackedPaths) (path: MirrorPath) : TrackedState voption =
        match tracked.TryGetValue(MirrorPath.value path) with
        | true, state -> ValueSome state
        | false, _ -> ValueNone

    /// The first candidate, in deleted-path order, that was not already consumed and whose content is exactly `hash`.
    let rec private firstMatch
        (consumed: HashSet<string>)
        (hash: ContentHash)
        (candidates: (MirrorPath * FileContent) list)
        : (MirrorPath * FileContent) voption =
        match candidates with
        | [] -> ValueNone
        | path, content as candidate :: rest ->
            if not (consumed.Contains(MirrorPath.value path)) && content.Hash.Equals hash then
                ValueSome candidate
            else
                firstMatch consumed hash rest

    /// Resolves a diff into a plan. `fullHashes` holds the exact content hash the shell computed for each added
    /// file that may be a move, by mirror path (case-insensitive); an added file absent from it is a plain add.
    /// A move is decided by an exact hash match only. Metadata operations run in scan order, then the deletions
    /// that no move consumed.
    let plan
        (tracked: TrackedPaths)
        (diff: DiffResult)
        (candidates: MoveCandidates)
        (fullHashes: Dictionary<string, ContentHash>)
        : BackupPlan =
        let consumed = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        let step (struct (metadata, transfers, totalBytes)) (change: PendingChange) =
            match change with
            | PendingChange.Link link ->
                let operation =
                    MetadataOperation.Link
                        { LinkAt = link.LinkPath
                          LinkTo = link.LinkTarget
                          Superseded = supersededHash (lookup tracked link.LinkPath) }

                struct (operation :: metadata, transfers, totalBytes)
            | PendingChange.Change file ->
                let transfer =
                    { Source = file
                      TransferKind = TransferKind.Changed(supersededHash (lookup tracked file.FilePath)) }

                struct (metadata, transfer :: transfers, totalBytes + file.Size)
            | PendingChange.Add file ->
                let moved =
                    match fullHashes.TryGetValue(MirrorPath.value file.FilePath), candidates.TryGetValue file.Size with
                    | (true, hash), (true, sameSize) -> firstMatch consumed hash sameSize
                    | _ -> ValueNone

                match moved with
                | ValueSome(fromPath, content) ->
                    consumed.Add(MirrorPath.value fromPath) |> ignore

                    let operation =
                        MetadataOperation.Move
                            { MoveTo = file.FilePath
                              MoveFrom = fromPath
                              Moved =
                                { content with
                                    Size = file.Size
                                    SourceModifiedAt = file.ModifiedAt } }

                    struct (operation :: metadata, transfers, totalBytes)
                | ValueNone ->
                    let transfer = { Source = file; TransferKind = TransferKind.Added }
                    struct (metadata, transfer :: transfers, totalBytes + file.Size)

        let struct (metadata, transfers, totalBytes) = List.fold step (struct ([], [], 0L)) diff.Pending

        // The deletions come last, after every move has had its chance to consume one.
        let metadata =
            diff.Deleted
            |> List.fold
                (fun metadata (path, state) ->
                    if consumed.Contains(MirrorPath.value path) then
                        metadata
                    else
                        MetadataOperation.Delete { DeletePath = path; Deleted = state } :: metadata)
                metadata

        { Metadata = List.rev metadata
          Transfers = List.rev transfers
          TotalBytes = totalBytes }
