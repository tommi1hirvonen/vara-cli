/// The interim plain-text summaries of `vara backup`: the first line is the headline, the rest is detail.
/// `fsharp-backup-presentation` replaces these with the full run summary.
module Vara.Cli.BackupSummary

open System
open System.Globalization
open System.Text
open Vara.Domain

let private byteUnits = [| "B"; "KB"; "MB"; "GB"; "TB" |]

let rec private scale (value: float) (unitIndex: int) : struct (float * int) =
    if value >= 1024.0 && unitIndex < byteUnits.Length - 1 then
        scale (value / 1024.0) (unitIndex + 1)
    else
        struct (value, unitIndex)

let formatBytes (bytes: int64) : string =
    match scale (float bytes) 0 with
    | struct (_, 0) -> $"{bytes} {byteUnits[0]}"
    | struct (value, unitIndex) ->
        let scaled = value.ToString("0.##", CultureInfo.InvariantCulture)
        $"{scaled} {byteUnits[unitIndex]}"

let private appendPaths (builder: StringBuilder) (paths: string list) : unit =
    if not (List.isEmpty paths) then
        builder.AppendLine $"  Failed ({List.length paths}):" |> ignore

        for path in paths do
            builder.AppendLine $"    - {path}" |> ignore

/// The summary of a finished run, which says whether it completed or was cancelled.
let formatRun (result: RunResult) : string =
    let builder = StringBuilder()
    let headline = if result.Status = SnapshotStatus.Cancelled then "Backup cancelled" else "Backup completed"
    builder.AppendLine headline |> ignore
    builder.AppendLine $"  Added:       {result.Totals.FilesAdded}" |> ignore
    builder.AppendLine $"  Changed:     {result.Totals.FilesChanged}" |> ignore
    builder.AppendLine $"  Moved:       {result.Totals.FilesMoved}" |> ignore
    builder.AppendLine $"  Deleted:     {result.Totals.FilesDeleted}" |> ignore
    builder.AppendLine $"  Transferred: {formatBytes result.Totals.BytesTransferred}" |> ignore
    appendPaths builder result.FailedPaths
    builder.ToString().TrimEnd('\r', '\n')

/// The summary of a dry run: what a real run would do and what it could not scan.
let formatPlan (summary: PlanSummary) : string =
    let builder = StringBuilder()
    builder.AppendLine "Dry run - no changes were made" |> ignore
    builder.AppendLine $"  Added:       {summary.Added}" |> ignore
    builder.AppendLine $"  Changed:     {summary.Changed}" |> ignore
    builder.AppendLine $"  Moved:       {summary.Moved}" |> ignore
    builder.AppendLine $"  Deleted:     {summary.Deleted}" |> ignore
    builder.AppendLine $"  To transfer: {formatBytes summary.PlannedBytes}" |> ignore
    appendPaths builder summary.Unscanned
    builder.ToString().TrimEnd('\r', '\n')
