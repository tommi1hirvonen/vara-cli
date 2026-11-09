/// Tests of count-based and byte-weighted progress, on `Display` values and with the clock passed in. Each module is
/// named after a requirement of the progress-reporting, retention-pruning or backup-integrity spec and each test after
/// one of its scenarios.
module Vara.Domain.Tests.CountProgressTests

open System
open Xunit
open Vara.Domain

let private start = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
let private after (milliseconds: int) : DateTimeOffset = start.AddMilliseconds(float milliseconds)
let private width = 100

let private step (ms: int) (event: ProgressEvent) (tracker: Tracker) : Tracker =
    Tracker.update Tracker.redrawInterval (after ms) event tracker

/// A tracker whose transfer phase began at time zero with the given totals.
let private transferring (bytes: int64) (items: int) : Tracker =
    Tracker.initial |> step 0 (TransferStarting(bytes, items))

let private text (wording: Display.Wording) (ms: int) (tracker: Tracker) : string list =
    Display.viewWith wording width (after ms) tracker |> Display.plainLines

let private bar (wording: Display.Wording) (ms: int) (tracker: Tracker) : BarRow =
    Display.viewWith wording width (after ms) tracker
    |> List.pick (function
        | Line.Bar bar -> Some bar
        | _ -> None)

/// The text of every line, joined: what a user reads.
let private screen (wording: Display.Wording) (ms: int) (tracker: Tracker) : string =
    text wording ms tracker |> String.concat "\n"

let private items = Display.pruneWording

module ``Count-based progress indication for non-byte-transfer operations`` =
    [<Fact>]
    let ``Total item count known upfront`` () =
        let tracker =
            transferring 0L 10
            |> step 200 FileTransferred
            |> step 400 FileTransferred
            |> step 600 FileTransferred

        Assert.Equal(30.0, Tracker.percentBy Basis.Items tracker, 6)
        let shown = screen items 1000 tracker
        Assert.Contains("Files 3 / 10", shown)
        // No byte count and no throughput: the work is not bytes.
        Assert.DoesNotContain("/s", shown)
        Assert.DoesNotContain(" B", shown)

    [<Fact>]
    let ``Total item count not yet known`` () =
        // Before the total is published the screen is the indeterminate bar, with the operation's own label.
        let indeterminate = bar items 0 Tracker.initial
        Assert.Equal(BarFill.Indeterminate, indeterminate.Fill)
        Assert.Contains("Evaluating", indeterminate.Label)

        // The count-based bar replaces it once the total is known.
        let known = bar items 100 (transferring 0L 4)
        Assert.Equal(BarFill.Filled 0.0, known.Fill)
        Assert.Contains("Deleting blobs", known.Label)

    [<Fact>]
    let ``Non-interactive or redirected output`` () =
        let tracker = transferring 0L 4 |> step 200 FileTransferred
        let line = Display.plainProgressWith Basis.Items (after 1000) tracker
        Assert.Contains("Files 1 / 4", line)
        Assert.Contains("25.0%", line)
        // Plain text carries no control sequence.
        Assert.False(line.Contains(char 27))

    [<Fact>]
    let ``the percentage of items is the share of items completed and never leaves 0 to 100`` () =
        let all = transferring 0L 3 |> step 200 FileTransferred |> step 400 FileTransferred |> step 600 FileTransferred
        Assert.Equal(100.0, Tracker.percentBy Basis.Items all, 6)
        // A fourth report, which the total does not allow, is capped.
        Assert.Equal(100.0, Tracker.percentBy Basis.Items (all |> step 800 FileTransferred), 6)

module ``Prune progress indication`` =
    [<Fact>]
    let ``Evaluating retention and determining unreferenced content`` () =
        let display = Display.viewWith items width (after 500) Tracker.initial
        let bar = display |> List.pick (function Line.Bar bar -> Some bar | _ -> None)
        Assert.Equal(BarFill.Indeterminate, bar.Fill)
        Assert.Contains("Evaluating", bar.Label)

    [<Fact>]
    let ``Removing unreferenced blobs`` () =
        let tracker = transferring 0L 8 |> step 200 FileTransferred |> step 400 FileTransferred
        Assert.Contains("Files 2 / 8", screen items 1000 tracker)
        Assert.Equal(25.0, Tracker.percentBy Basis.Items tracker, 6)

module ``Check reports workload progress and estimated time remaining`` =
    let private full = Display.checkWording
    let private quick = Display.quickCheckWording

    [<Fact>]
    let ``Full check reports byte-weighted progress`` () =
        // One blob of 900 bytes and nine of 10: 1 of 10 blobs is 90% of the bytes.
        let tracker = transferring 990L 10 |> step 200 (BytesCopied 900L) |> step 400 FileTransferred
        Assert.Equal(900.0 / 990.0 * 100.0, Tracker.percentBy Basis.Bytes tracker, 6)
        let shown = screen full 1000 tracker
        Assert.Contains("Files 1 / 10", shown)
        Assert.Contains("900 B / 990 B", shown)
        // The ETA comes from the observed throughput: 900 B in one second leaves 90 B for 0.1 s.
        Assert.Equal(TimeSpan.FromSeconds 0.1, (Tracker.remainingBy Basis.Bytes (after 1000) tracker).Value)

    [<Fact>]
    let ``Missing blob advances full-check progress`` () =
        let tracker =
            transferring 1000L 2
            |> step 200 (WorkCredited 600L)
            |> step 400 FileTransferred

        // The percentage moves by the credited size, the count by one blob, and no byte counts as read.
        Assert.Equal(60.0, Tracker.percentBy Basis.Bytes tracker, 6)
        Assert.Equal(1, tracker.Shown.Files)
        Assert.Equal(0L, tracker.Shown.Bytes)
        Assert.Equal(0.0, Tracker.throughput (after 1000) tracker, 6)

    [<Fact>]
    let ``Quick check reports count-based progress and ETA`` () =
        // The bytes do not matter to a quick check: three of ten blobs in one second is 30%, 7 s to go.
        let tracker =
            transferring 123_456L 10
            |> step 200 FileTransferred
            |> step 400 FileTransferred
            |> step 600 FileTransferred

        Assert.Equal(30.0, Tracker.percentBy Basis.Items tracker, 6)
        let left = (Tracker.remainingBy Basis.Items (after 1000) tracker).Value
        Assert.Equal(7.0 / 3.0, left.TotalSeconds * 1.0, 0.5) // 7 blobs at 3 per second
        Assert.Contains("Files 3 / 10", screen quick 1000 tracker)

    [<Fact>]
    let ``Estimate is not yet available`` () =
        // Nothing has been observed, so the ETA reads as calculating, in both modes.
        let started = transferring 1000L 10
        Assert.Equal(ValueNone, Tracker.remainingBy Basis.Bytes (after 1000) started)
        Assert.Equal(ValueNone, Tracker.remainingBy Basis.Items (after 1000) started)
        Assert.Contains("calculating...", screen full 1000 started)
        Assert.Contains("calculating...", screen quick 1000 started)

    [<Fact>]
    let ``Empty or zero-size workload completes cleanly`` () =
        // No blobs at all, and blobs whose recorded sizes are all zero: 100% and no division by zero.
        for tracker in [ transferring 0L 0; transferring 0L 5 |> step 200 FileTransferred ] do
            let percent = Tracker.percentBy Basis.Bytes tracker
            Assert.Equal(100.0, percent, 6)
            Assert.False(Double.IsNaN percent)

        let ended = transferring 0L 0 |> step 300 (RunEnded Outcome.Success)
        Assert.Equal(BarFill.Filled 100.0, (bar full 1000 ended).Fill)

    [<Fact>]
    let ``Full check progress in plain text names the percentage the count and the estimate`` () =
        let tracker = transferring 1000L 4 |> step 200 (BytesCopied 500L) |> step 400 FileTransferred
        let line = Display.plainProgressWith Basis.Bytes (after 1000) tracker
        Assert.Contains("50.0%", line)
        Assert.Contains("Files 1 / 4", line)
        Assert.Contains("ETA", line)

    [<Fact>]
    let ``the terminal indicator follows the basis of the run`` () =
        let tracker = transferring 0L 4 |> step 200 FileTransferred
        // By bytes an empty total reads as complete; by items one blob of four is a quarter.
        Assert.Equal(Indicator.Percentage 100, TerminalProgress.indicatorBy Basis.Bytes tracker)
        Assert.Equal(Indicator.Percentage 25, TerminalProgress.indicatorBy Basis.Items tracker)
