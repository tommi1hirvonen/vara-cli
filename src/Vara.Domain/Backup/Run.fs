namespace Vara.Domain

open System

/// A file whose content is stored and placed. Carries the scan-time modification time, never a re-read one.
type TransferredFile =
    { Path: MirrorPath
      Content: FileContent
      Kind: ChangeKind }

/// What one executed operation reports back to the run. The shell produces these; the fold consumes them.
[<RequireQualifiedAccess>]
type Completion =
    | Moved of move: MoveOperation
    | Deleted of delete: DeleteOperation
    | Linked of link: LinkOperation
    | Transferred of transferred: TransferredFile
    /// The operation failed with an I/O error, or its mirror path escaped the target root.
    /// Its previous manifest state stays as it was.
    | Failed of path: MirrorPath

/// The totals of a run so far. A struct, so counting a completion allocates nothing of its own.
[<Struct>]
type Counts =
    { FilesAdded: int
      FilesChanged: int
      FilesMoved: int
      FilesDeleted: int
      FilesFailed: int
      BytesTransferred: int64 }

/// The run's state. Immutable: each completion yields a new value, so one fold over the completions is the whole
/// evolution. Cost per completion: one record of 56 bytes (header, the 32-byte `Counts`, the failed-path list, the
/// checkpoint time), plus a 32-byte list cell for a failure, plus the rows the completion produces.
type RunState =
    { Counts: Counts
      /// Newest first; reversed when the run ends.
      FailedPaths: MirrorPath list
      LastCheckpoint: DateTimeOffset }

/// How a snapshot ends. A run stopped by Ctrl+C is `Cancelled`, which differs from `Failed`.
[<RequireQualifiedAccess>]
type SnapshotStatus =
    | Running
    | Complete
    | Failed
    | Cancelled

/// The result of a run that was not refused: how it ended, its totals, and the paths that failed.
/// Scan failures come first, as the source-relative text the scan reported; then failed operations, as mirror paths.
type RunResult =
    { SnapshotId: int64
      Status: SnapshotStatus
      Totals: Counts
      FailedPaths: string list }

/// How far a run's transfers have come, against the plan's totals.
[<Struct>]
type Progress =
    { BytesTransferred: int64
      TotalBytes: int64
      FilesTransferred: int
      TotalFiles: int }

module Run =
    let initial (now: DateTimeOffset) : RunState =
        { Counts =
            { FilesAdded = 0
              FilesChanged = 0
              FilesMoved = 0
              FilesDeleted = 0
              FilesFailed = 0
              BytesTransferred = 0L }
          FailedPaths = []
          LastCheckpoint = now }

    let private row (path: MirrorPath) (previous: MirrorPath voption) (content: RowContent) (kind: ChangeKind) (at: DateTimeOffset) : ManifestRow =
        { RowPath = path
          PreviousPath = previous
          Content = content
          Kind = kind
          RecordedAt = at }

    /// A deleted entry's row keeps the content it had. A deleted link keeps no target: the C# build records none.
    let private contentOfTracked (state: TrackedState) : RowContent =
        match state with
        | TrackedState.Regular content -> RowContent.Regular content
        | TrackedState.Linked _ -> RowContent.Linked ValueNone

    /// Applies one completed operation: the new state and the manifest rows to record under the run's snapshot,
    /// stamped `recordedAt`. A move records the old path as deleted and the new path as moved. A link adds a row
    /// and counts nothing. A failure adds no row, so the path keeps its previous manifest state.
    let apply (recordedAt: DateTimeOffset) (state: RunState) (completion: Completion) : struct (RunState * ManifestRow list) =
        let counts = state.Counts

        match completion with
        | Completion.Moved move ->
            let content = RowContent.Regular move.Moved

            struct ({ state with Counts = { counts with FilesMoved = counts.FilesMoved + 1 } },
                    [ row move.MoveFrom ValueNone content ChangeKind.Deleted recordedAt
                      row move.MoveTo (ValueSome move.MoveFrom) content ChangeKind.Moved recordedAt ])
        | Completion.Deleted delete ->
            struct ({ state with Counts = { counts with FilesDeleted = counts.FilesDeleted + 1 } },
                    [ row delete.DeletePath ValueNone (contentOfTracked delete.Deleted) ChangeKind.Deleted recordedAt ])
        | Completion.Linked link ->
            struct (state, [ row link.LinkAt ValueNone (RowContent.Linked link.LinkTo) ChangeKind.Linked recordedAt ])
        | Completion.Transferred transferred ->
            let counts =
                match transferred.Kind with
                | ChangeKind.Added -> { counts with FilesAdded = counts.FilesAdded + 1 }
                | ChangeKind.Changed
                | ChangeKind.Moved
                | ChangeKind.Deleted
                | ChangeKind.Linked -> { counts with FilesChanged = counts.FilesChanged + 1 }

            let counts =
                { counts with BytesTransferred = counts.BytesTransferred + transferred.Content.Size }

            struct ({ state with Counts = counts },
                    [ row transferred.Path ValueNone (RowContent.Regular transferred.Content) transferred.Kind recordedAt ])
        | Completion.Failed path ->
            struct ({ state with
                        Counts = { counts with FilesFailed = counts.FilesFailed + 1 }
                        FailedPaths = path :: state.FailedPaths },
                    [])

    /// A checkpoint is due once the interval has fully elapsed since the last one. The clock is passed in.
    let checkpointDue (interval: TimeSpan) (now: DateTimeOffset) (state: RunState) : bool =
        now - state.LastCheckpoint >= interval

    let checkpointed (now: DateTimeOffset) (state: RunState) : RunState = { state with LastCheckpoint = now }

    /// The progress shown while transfers run. `bytesCopied` counts chunks as they are copied, which runs ahead of
    /// the bytes of completed files; files count once their row is recorded.
    let progress (totalBytes: int64) (totalFiles: int) (bytesCopied: int64) (state: RunState) : Progress =
        { BytesTransferred = bytesCopied
          TotalBytes = totalBytes
          FilesTransferred = state.Counts.FilesAdded + state.Counts.FilesChanged
          TotalFiles = totalFiles }

    /// A run stopped by Ctrl+C ends `Cancelled`; otherwise `Complete`.
    let endStatus (cancelRequested: bool) : SnapshotStatus =
        if cancelRequested then SnapshotStatus.Cancelled else SnapshotStatus.Complete

    /// The result of a run whose execution returned: scan failures join the executor's. The failed-path list
    /// puts scan failures first, then failed operations oldest first.
    let finish (snapshotId: int64) (cancelRequested: bool) (scanFailures: ScanFailure list) (state: RunState) : RunResult =
        let scanFailureCount = List.length scanFailures

        { SnapshotId = snapshotId
          Status = endStatus cancelRequested
          Totals =
            { state.Counts with
                FilesFailed = state.Counts.FilesFailed + scanFailureCount }
          FailedPaths =
            (scanFailures |> List.map _.Reported)
            @ (state.FailedPaths |> List.rev |> List.map MirrorPath.value) }

    /// What a dry run reports: the plan's counts and bytes, and the paths a real run would skip. A link is neither
    /// added nor changed, as in a real run's totals.
    let summarizePlan (scanFailures: ScanFailure list) (plan: BackupPlan) : PlanSummary =
        let countTransfers (kind: TransferKind -> bool) = plan.Transfers |> List.filter (fun transfer -> kind transfer.TransferKind) |> List.length

        let countMetadata (isKind: MetadataOperation -> bool) = plan.Metadata |> List.filter isKind |> List.length

        let added = countTransfers (function TransferKind.Added -> true | TransferKind.Changed _ -> false)
        let changed = plan.Transfers.Length - added
        let moved = countMetadata (function MetadataOperation.Move _ -> true | _ -> false)
        let deleted = countMetadata (function MetadataOperation.Delete _ -> true | _ -> false)

        { Added = added
          Changed = changed
          Moved = moved
          Deleted = deleted
          PlannedBytes = plan.TotalBytes
          Unscanned = scanFailures |> List.map _.Reported }

    /// How a run that was not refused ends the command: failed paths or a cancellation give `PartialFailure`.
    let outcome (result: RunResult) : Outcome =
        match result.Status, result.FailedPaths with
        | SnapshotStatus.Cancelled, _
        | _, _ :: _ -> Outcome.PartialFailure
        | _, [] -> Outcome.Success
