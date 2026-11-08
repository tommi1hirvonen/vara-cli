namespace Vara.Domain

open System

/// A JSON document as data, so the output is built by a pure function and written once.
[<RequireQualifiedAccess>]
type JsonValue =
    | String of string
    | Bool of bool
    | Number of int64
    | Time of DateTimeOffset
    | Array of JsonValue list
    /// Members in the order they are written.
    | Object of (string * JsonValue) list

/// The JSON of `vara backup --json`, for a real run and for a dry run. The members and their order are the C# build's
/// `BackupRunSummaryJson`.
module RunJson =
    let private document
        (mode: string)
        (profile: string)
        (startedAt: DateTimeOffset)
        (completedAt: DateTimeOffset)
        (cancelled: bool)
        (added: int)
        (changed: int)
        (moved: int)
        (deleted: int)
        (failedPaths: string list)
        (failed: int)
        (bytes: int64)
        : JsonValue =
        JsonValue.Object
            [ "mode", JsonValue.String mode
              "profile", JsonValue.String profile
              "startedAt", JsonValue.Time startedAt
              "completedAt", JsonValue.Time completedAt
              "cancelled", JsonValue.Bool cancelled
              "added", JsonValue.Number(int64 added)
              "changed", JsonValue.Number(int64 changed)
              "moved", JsonValue.Number(int64 moved)
              "deleted", JsonValue.Number(int64 deleted)
              "failed", JsonValue.Number(int64 failed)
              "failedPaths", JsonValue.Array(failedPaths |> List.map JsonValue.String)
              "bytesTransferred", JsonValue.Number bytes ]

    let ofRun (profile: string) (startedAt: DateTimeOffset) (completedAt: DateTimeOffset) (result: RunResult) : JsonValue =
        document
            "executed"
            profile
            startedAt
            completedAt
            (result.Status = SnapshotStatus.Cancelled)
            result.Totals.FilesAdded
            result.Totals.FilesChanged
            result.Totals.FilesMoved
            result.Totals.FilesDeleted
            result.FailedPaths
            result.Totals.FilesFailed
            result.Totals.BytesTransferred

    /// A dry run executes nothing, so what failed is only what the scan could not read.
    let ofPlan (profile: string) (startedAt: DateTimeOffset) (completedAt: DateTimeOffset) (summary: PlanSummary) : JsonValue =
        document
            "dry-run"
            profile
            startedAt
            completedAt
            false
            summary.Added
            summary.Changed
            summary.Moved
            summary.Deleted
            summary.Unscanned
            (List.length summary.Unscanned)
            summary.PlannedBytes
