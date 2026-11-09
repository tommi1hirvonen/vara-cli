/// Tests for the evolution of a run's state, built from plain lists of completions.
module Vara.Domain.Tests.RunTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain
open Vara.Domain.Tests.Fixtures

let private recordedAt = at 100

let private transferred (path: string) (kind: ChangeKind) (size: int64) : Completion =
    Completion.Transferred
        { Path = mirror path
          Content = content "h" size 1
          Kind = kind }

let private moved (toPath: string) (fromPath: string) : Completion =
    Completion.Moved
        { MoveTo = mirror toPath
          MoveFrom = mirror fromPath
          Moved = content "h" 5L 1 }

let private deleted (path: string) (state: TrackedState) : Completion =
    Completion.Deleted { DeletePath = mirror path; Deleted = state }

let private failed (path: string) : Completion = Completion.Failed(mirror path)

/// Folds the completions in order, returning the final state and every row, in the order they were produced.
let private foldAll (completions: Completion list) : RunState * ManifestRow list =
    let mutable state = Run.initial (at 0)
    let mutable rows = []

    for completion in completions do
        let struct (next, produced) = Run.apply recordedAt state completion
        state <- next
        rows <- rows @ produced

    state, rows

let private kinds (rows: ManifestRow list) : ChangeKind list = rows |> List.map _.Kind

module ``Applying completed operations`` =
    [<Fact>]
    let ``A transferred file counts its kind and bytes and records one row`` () =
        let state, rows =
            foldAll [ transferred "C\\a" ChangeKind.Added 10L; transferred "C\\b" ChangeKind.Changed 4L ]

        Assert.Equal(1, state.Counts.FilesAdded)
        Assert.Equal(1, state.Counts.FilesChanged)
        Assert.Equal(14L, state.Counts.BytesTransferred)
        Assert.Equal<ChangeKind list>([ ChangeKind.Added; ChangeKind.Changed ], kinds rows)
        Assert.All(rows, fun r -> Assert.True(r.RecordedAt.Equals recordedAt))

    [<Fact>]
    let ``A move records the old path as deleted and the new path as moved`` () =
        let state, rows = foldAll [ moved "C\\new" "C\\old" ]
        Assert.Equal(1, state.Counts.FilesMoved)

        match rows with
        | [ gone; arrived ] ->
            Assert.Equal<ChangeKind list>([ ChangeKind.Deleted; ChangeKind.Moved ], kinds rows)
            Assert.Equal("C\\old", MirrorPath.value gone.RowPath)
            Assert.Equal("C\\new", MirrorPath.value arrived.RowPath)
            Assert.Equal(ValueSome(mirror "C\\old"), arrived.PreviousPath)
            Assert.Equal(ValueNone, gone.PreviousPath)
        | _ -> Assert.Fail "a move records two rows"

    [<Fact>]
    let ``A deletion records the content it had, and a deleted link records no target`` () =
        let state, rows =
            foldAll [ deleted "C\\f" (regular "h" 3L 1); deleted "C\\l" (TrackedState.Linked(ValueSome "t")) ]

        Assert.Equal(2, state.Counts.FilesDeleted)

        match rows with
        | [ file; link ] ->
            Assert.True(match file.Content with RowContent.Regular _ -> true | _ -> false)
            Assert.True(match link.Content with RowContent.Linked ValueNone -> true | _ -> false)
        | _ -> Assert.Fail "two rows"

    [<Fact>]
    let ``A link records a row and counts nothing`` () =
        let state, rows =
            foldAll [ Completion.Linked { LinkAt = mirror "C\\l"; LinkTo = ValueSome "t"; Superseded = ValueNone } ]

        Assert.Equal(Run.initial (at 0), state)
        Assert.Equal<ChangeKind list>([ ChangeKind.Linked ], kinds rows)

    [<Fact>]
    let ``A failure records no row and keeps the path`` () =
        let state, rows = foldAll [ failed "C\\x"; failed "C\\y" ]
        Assert.Empty rows
        Assert.Equal(2, state.Counts.FilesFailed)
        Assert.Equal<string list>([ "C\\y"; "C\\x" ], state.FailedPaths |> List.map MirrorPath.value)

module Checkpoints =
    [<Fact>]
    let ``A checkpoint is due once the interval has elapsed`` () =
        let state = Run.initial (at 0)
        let interval = TimeSpan.FromMinutes 1.0
        Assert.False(Run.checkpointDue interval (state.LastCheckpoint.AddSeconds 59.0) state)
        Assert.True(Run.checkpointDue interval (state.LastCheckpoint.AddSeconds 60.0) state)

    [<Fact>]
    let ``Checkpointing moves the clock`` () =
        let state = Run.initial (at 0) |> Run.checkpointed (at 5)
        Assert.False(Run.checkpointDue (TimeSpan.FromMinutes 1.0) (at 5) state)

module ``Progress and outcome`` =
    [<Fact>]
    let ``Progress counts transferred files against the plan`` () =
        let state, _ = foldAll [ transferred "C\\a" ChangeKind.Added 10L; moved "C\\n" "C\\o" ]
        let progress = Run.progress 100L 4 30L state
        Assert.Equal(30L, progress.BytesTransferred)
        Assert.Equal(100L, progress.TotalBytes)
        Assert.Equal(1, progress.FilesTransferred)
        Assert.Equal(4, progress.TotalFiles)

    [<Fact>]
    let ``A run asked to stop ends cancelled, otherwise complete`` () =
        Assert.Equal(SnapshotStatus.Cancelled, Run.endStatus true)
        Assert.Equal(SnapshotStatus.Complete, Run.endStatus false)

    [<Fact>]
    let ``Scan failures join the failed operations in the result`` () =
        let state, _ = foldAll [ failed "C\\x"; failed "C\\y"; transferred "C\\a" ChangeKind.Added 1L ]
        let scanFailures = [ failure "C\\Users\\john" ScanFailureReason.UnreadableDirectory ]
        let result = Run.finish 1L false scanFailures state
        Assert.Equal(3, result.Totals.FilesFailed)
        Assert.Equal<string list>([ "C\\Users\\john"; "C\\x"; "C\\y" ], result.FailedPaths)
        Assert.Equal(SnapshotStatus.Complete, result.Status)

module ``Properties of the run state`` =
    let private completionFor (index: int) : Gen<Completion> =
        let path = $"C\\f{index}"

        Gen.oneof
            [ Gen.choose (0, 1000) |> Gen.map (fun size -> transferred path ChangeKind.Added (int64 size))
              Gen.choose (0, 1000) |> Gen.map (fun size -> transferred path ChangeKind.Changed (int64 size))
              Gen.constant (moved path $"C\\old{index}")
              Gen.constant (deleted path (regular "h" 1L 1))
              Gen.constant (failed path) ]

    /// Per-file sequences, plus a random choice of which file's next completion comes next.
    let private scenario =
        gen {
            let! files = Gen.choose (1, 6)

            let sequenceFor (i: int) =
                gen {
                    let! n = Gen.choose (1, 3)
                    return! Gen.listOfLength n (completionFor i)
                }

            let rec build (i: int) (acc: Completion list list) =
                gen {
                    if i = files then
                        return List.rev acc
                    else
                        let! next = sequenceFor i
                        return! build (i + 1) (next :: acc)
                }

            let! sequences = build 0 []

            let! picks = Gen.listOfLength 40 (Gen.choose (0, files - 1))
            return sequences, picks
        }

    let private interleave (sequences: Completion list list) (picks: int list) : Completion list =
        let queues = sequences |> List.toArray
        let result = ResizeArray()

        for pick in picks do
            // The next non-empty queue at or after the pick, so every pick yields a completion while any remain.
            let mutable index = pick % queues.Length
            let mutable tries = 0

            while List.isEmpty queues[index] && tries < queues.Length do
                index <- (index + 1) % queues.Length
                tries <- tries + 1

            match queues[index] with
            | next :: rest ->
                result.Add next
                queues[index] <- rest
            | [] -> ()

        for queue in queues do
            result.AddRange queue

        List.ofSeq result

    [<Property>]
    let ``any interleaving that keeps each file's own order yields the same counts, bytes and failed paths`` () =
        Prop.forAll (Arb.fromGen scenario) (fun (sequences, picks) ->
            let sequential = List.concat sequences
            let shuffled = interleave sequences picks
            let first, _ = foldAll sequential
            let second, _ = foldAll shuffled
            let sorted (state: RunState) = state.FailedPaths |> List.map MirrorPath.value |> List.sort
            first.Counts = second.Counts && sorted first = sorted second)

module ``Dry-run mode`` =
    let private summarize (state: (string * TrackedState) list) (scan: ScanEntry list) =
        let tracked = tracked state
        let diff = Diff.run tracked scan
        let plan = Planning.plan tracked diff (Planning.moveCandidates diff.Deleted) (System.Collections.Generic.Dictionary())
        Run.summarizePlan diff.Failures plan

    [<Fact>]
    let ``No changes pending`` () =
        let summary = summarize [ ("C\a", regular "h" 1L 1) ] [ ScanEntry.File(file "C\a" 1L 1) ]
        Assert.Equal(0, summary.Added + summary.Changed + summary.Moved + summary.Deleted)
        Assert.Equal(0L, summary.PlannedBytes)
        Assert.Empty summary.Unscanned
        Assert.Equal(Outcome.Success, Run.outcome (Run.finish 1L false [] (Run.initial (at 0))))

    [<Fact>]
    let ``Dry run reports what a real run would add, change and delete`` () =
        let summary =
            summarize
                [ "C\a", regular "h" 1L 1; "C\gone", regular "g" 9L 1 ]
                [ ScanEntry.File(file "C\a" 2L 1)
                  ScanEntry.File(file "C\b" 7L 1)
                  ScanEntry.Link(link "C\l" ValueNone) ]

        Assert.Equal(1, summary.Added)
        Assert.Equal(1, summary.Changed)
        Assert.Equal(1, summary.Deleted)
        Assert.Equal(0, summary.Moved)
        Assert.Equal(9L, summary.PlannedBytes)

    [<Fact>]
    let ``Dry run reports unreadable paths without treating them as an error`` () =
        let summary =
            summarize [] [ ScanEntry.Failure(failure "C\locked" ScanFailureReason.UnreadableEntry) ]

        Assert.Equal<string list>([ "C\locked" ], summary.Unscanned)

module ``Mapping a run to an outcome`` =
    let private resultOf (cancel: bool) (completions: Completion list) (scanFailures: ScanFailure list) =
        let state, _ = foldAll completions
        Run.finish 1L cancel scanFailures state

    [<Fact>]
    let ``A clean run is a success`` () =
        Assert.Equal(Outcome.Success, Run.outcome (resultOf false [ transferred "C\a" ChangeKind.Added 1L ] []))

    [<Fact>]
    let ``Failed paths give a partial failure`` () =
        Assert.Equal(Outcome.PartialFailure, Run.outcome (resultOf false [ failed "C\a" ] []))

        Assert.Equal(
            Outcome.PartialFailure,
            Run.outcome (resultOf false [] [ failure "C\d" ScanFailureReason.UnreadableDirectory ])
        )

    [<Fact>]
    let ``Cancellation gives a partial failure`` () =
        Assert.Equal(Outcome.PartialFailure, Run.outcome (resultOf true [] []))
