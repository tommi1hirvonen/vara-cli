/// `Retention.evaluate` against the recorded decisions of the C# evaluator, over generated timestamps that sit around day,
/// ISO-Monday week, month and year boundaries, in non-zero offsets. A line of `cases.txt` is the snapshots, the policy, the
/// kept ids and the eligible ids, separated by a tab (see the recorder for the fields).
module Vara.Engine.Tests.RetentionCorpusTests

open System
open System.Globalization
open Xunit
open Vara.Domain
open Vara.Testing

let private recordedCases = 600

let private status (text: string) : SnapshotStatus =
    match text with
    | "Running" -> SnapshotStatus.Running
    | "Complete" -> SnapshotStatus.Complete
    | "Failed" -> SnapshotStatus.Failed
    | "Cancelled" -> SnapshotStatus.Cancelled
    | other -> failwith $"unknown status {other}"

/// A snapshot from `id|completed-at|status`.
let private snapshotOf (entry: string) : SnapshotInfo =
    match entry.Split '|' with
    | [| id; completed; kind |] ->
        let completedAt = DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

        { SnapshotId = int64 id
          StartedAt = completedAt.AddMinutes -1.0
          CompletedAt = ValueSome completedAt
          Status = status kind
          Stats =
            { FilesAdded = 0
              FilesChanged = 0
              FilesMoved = 0
              FilesDeleted = 0
              FilesFailed = 0
              BytesTransferred = 0L } }
    | _ -> failwith $"malformed snapshot entry {entry}"

let private policyOf (text: string) : RetentionPolicy =
    let count (value: string) = (RetentionCount.tryCreate (int value)).Value

    match text.Split '/' with
    | [| daily; weekly; monthly; yearly |] ->
        { KeepDaily = count daily
          KeepWeekly = count weekly
          KeepMonthly = count monthly
          KeepYearly = count yearly }
    | _ -> failwith $"malformed policy {text}"

let private idsText (ids: seq<int64>) : string = ids |> Seq.sort |> Seq.map string |> String.concat ","

let private decisionText (kept: seq<int64>) (eligible: seq<int64>) : string =
    $"kept [{idsText kept}] eligible [{idsText eligible}]"

/// The decision for one recorded line, as the line's own two id fields, so that one comparison covers kept and eligible.
let private decide (line: string) : string =
    match line.Split '\t' with
    | [| snapshots; policy; _; _ |] ->
        let decision = Retention.evaluate (policyOf policy) (snapshots.Split ';' |> Array.map snapshotOf |> List.ofArray)
        decisionText decision.Kept decision.Eligible
    | _ -> failwith $"malformed case {line}"

let private recorded (line: string) : string =
    match line.Split '\t' with
    | [| _; _; kept; eligible |] -> $"kept [{kept}] eligible [{eligible}]"
    | _ -> failwith $"malformed case {line}"

let private lines: string list =
    (Corpus.read "retention" "cases.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

[<Fact>]
let ``the kept and eligible sets equal the recorded decisions of the C# evaluator`` () =
    let cases = lines |> List.mapi (fun index line -> $"case {index + 1}: {line}", recorded line, decide line)
    Corpus.assertSame cases

[<Fact>]
let ``the recorded cases keep their number and reach every boundary kind`` () =
    Assert.Equal(recordedCases, lines.Length)

    // A guard on the corpus: it must hold instants on both sides of each boundary, in non-zero offsets.
    let instantsOf (line: string) : DateTimeOffset list =
        let snapshots: string = (line.Split '\t')[0]

        [ for entry in snapshots.Split ';' ->
              let completed: string = (entry.Split '|')[1]
              DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) ]

    let instants = lines |> List.collect instantsOf

    let utcDates = instants |> List.map (fun instant -> DateOnly.FromDateTime instant.UtcDateTime) |> List.distinct
    Assert.Contains(DateOnly(2025, 12, 31), utcDates)
    Assert.Contains(DateOnly(2026, 1, 1), utcDates)
    Assert.Contains(DateOnly(2026, 3, 1), utcDates)
    Assert.Contains(DateOnly(2026, 3, 2), utcDates)
    Assert.True(instants |> List.exists (fun instant -> instant.Offset <> TimeSpan.Zero))
