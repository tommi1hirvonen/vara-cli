/// Tests of the progress display, on `Display` values and with the clock passed in. Each module is named after a
/// requirement of openspec/specs/progress-reporting/spec.md and each test after one of its scenarios.
module Vara.Domain.Tests.ProgressTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain
open Vara.Domain.Tests.Fixtures

let private start = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
let private after (milliseconds: int) : DateTimeOffset = start.AddMilliseconds(float milliseconds)
let private interval = Tracker.redrawInterval

let private feed (steps: (int * ProgressEvent) list) : Tracker =
    steps |> List.fold (fun tracker (ms, event) -> Tracker.update interval (after ms) event tracker) Tracker.initial

/// A tracker whose transfer phase has begun with the given totals at time zero.
let private transferring (bytes: int64) (files: int) : Tracker = feed [ 0, TransferStarting(bytes, files) ]

let private step (ms: int) (event: ProgressEvent) (tracker: Tracker) : Tracker =
    Tracker.update interval (after ms) event tracker

let private width = 100

let private lines (display: Display) : string list =
    display
    |> List.map (function
        | Line.Bar bar -> bar.Label + "|" + bar.Trailing
        | Line.Row cells -> cells |> List.map (fun cell -> cell.Text) |> String.concat ""
        | Line.Text(_, text) -> text)

let private barOf (display: Display) : BarRow =
    display |> List.pick (function Line.Bar bar -> Some bar | _ -> None)

let private rowsOf (display: Display) : Cell list list =
    display |> List.choose (function Line.Row cells -> Some cells | _ -> None)

let private textLines (display: Display) : string list =
    display |> List.choose (function Line.Text(_, text) -> Some text | _ -> None)

let private shown (tracker: Tracker) : Display = Display.view width (after 1000) tracker

module ``Byte-based progress`` =
    [<Fact>]
    let ``Mixed file sizes`` () =
        // One file of 900 bytes and ninety-nine of one byte: 1 of 100 files done is not 1% of the bytes.
        let tracker = transferring 999L 100 |> step 200 (BytesCopied 900L) |> step 400 FileTransferred
        Assert.Equal(900.0 / 999.0 * 100.0, Tracker.percent tracker, 6)
        Assert.Equal(1, tracker.Shown.Files)

module ``Upfront work estimation`` =
    [<Fact>]
    let ``Progress shown at start of transfer`` () =
        let display = shown (transferring 2048L 3)
        Assert.Contains("0 B / 2 KB", textLines display @ (rowsOf display |> List.map (List.map (fun c -> c.Text) >> String.concat "")) |> String.concat "\n")
        Assert.Contains("ETA calculating...", lines display |> String.concat "\n")

module ``Scan-phase progress indication`` =
    [<Fact>]
    let ``Scanning and hashing before transfer begins`` () =
        let tracker = feed [ 0, ScanCounted 300 ]
        let bar = barOf (shown tracker)
        Assert.Equal(BarFill.Indeterminate, bar.Fill)
        Assert.Equal(Tone.Neutral, bar.Tone)

    [<Fact>]
    let ``Indicator replaced once transfer begins`` () =
        let tracker = feed [ 0, ScanCounted 300; 10, TransferStarting(100L, 1) ]
        let bar = barOf (shown tracker)
        Assert.Equal(BarFill.Filled 0.0, bar.Fill)
        Assert.Equal(2, rowsOf (shown tracker) |> List.length)

module ``Dry-run scan progress indication`` =
    let private live = { IsTerminal = true; SupportsColor = true; NoColorRequested = false }

    [<Fact>]
    let ``Dry-run planning in a live terminal`` () =
        Assert.Equal(OutputMode.Live, OutputMode.choose false live)
        Assert.Equal(BarFill.Indeterminate, (barOf (shown (feed [ 0, ScanCounted 5 ]))).Fill)
        // Once planning is over the screen is the summary, which has no bar.
        let summary =
            Display.ofPlan { Added = 1; Changed = 0; Moved = 0; Deleted = 0; PlannedBytes = 5L; Unscanned = [] }

        Assert.DoesNotContain(summary, fun line -> match line with Line.Bar _ -> true | _ -> false)

    [<Fact>]
    let ``Dry-run JSON output`` () =
        Assert.Equal(OutputMode.Json, OutputMode.choose true live)
        Assert.False(TerminalProgress.enabled OutputMode.Json "session")

    [<Fact>]
    let ``Dry-run with redirected output`` () =
        Assert.Equal(OutputMode.Plain, OutputMode.choose false { live with IsTerminal = false })

module ``Throughput and ETA reporting`` =
    [<Fact>]
    let ``Long-running transfer`` () =
        let tracker = transferring 1000L 1 |> step 1000 (BytesCopied 250L)
        let display = Display.view width (after 1000) tracker
        let second = rowsOf display |> List.item 1 |> List.map (fun cell -> cell.Text.TrimEnd())
        Assert.Equal<string list>([ "250 B/s"; "ETA 3s" ], second)
        // Later, with more bytes, both figures move.
        let later = tracker |> step 2000 (BytesCopied 500L)
        let rowLater = rowsOf (Display.view width (after 2000) later) |> List.item 1 |> List.map (fun cell -> cell.Text.TrimEnd())
        Assert.Equal<string list>([ "375 B/s"; "ETA 0.7s" ], rowLater)

module ``Progress bar occupies a dedicated, width-sized line`` =
    [<Fact>]
    let ``Normal interactive terminal`` () =
        let display = Display.view 120 (after 1000) (transferring 100L 1)

        match display with
        | Line.Bar bar :: Line.Row _ :: Line.Row _ :: _ ->
            Assert.Equal(120 - Display.labelWidth - Display.trailingWidth, bar.Width)
        | other -> failwith $"unexpected layout: {lines other}"

    [<Fact>]
    let ``Terminal width cannot be determined`` () =
        // Redirected output has no width: the mode is plain, which never draws a bar.
        let redirected = { IsTerminal = false; SupportsColor = true; NoColorRequested = false }
        Assert.Equal(OutputMode.Plain, OutputMode.choose false redirected)

module ``Progress bar row includes a leading phase description`` =
    [<Fact>]
    let ``Scan phase in progress`` () =
        let bar = barOf (shown (feed [ 0, ScanCounted 1 ]))
        Assert.StartsWith(" Scanning files...", bar.Label)
        Assert.Equal(Display.trailingWidth, bar.Trailing.Length)
        Assert.Equal("", bar.Trailing.Trim())

    [<Fact>]
    let ``Transfer phase in progress`` () =
        let bar = barOf (shown (transferring 100L 1 |> step 200 (BytesCopied 50L)))
        Assert.StartsWith(" Backing up...", bar.Label)
        Assert.Equal(" 50.0%".PadLeft(Display.trailingWidth), bar.Trailing)

    [<Fact>]
    let ``Bar length is consistent across phases`` () =
        let scan = barOf (shown (feed [ 0, ScanCounted 1 ]))
        let transfer = barOf (shown (transferring 100L 1))
        Assert.Equal(scan.Width, transfer.Width)
        Assert.Equal(scan.Label.Length, transfer.Label.Length)
        Assert.Equal(scan.Trailing.Length, transfer.Trailing.Length)

module ``Numeric progress fields do not shift layout as values change`` =
    [<Fact>]
    let ``A displayed value crosses a digit-count boundary`` () =
        let before = transferring 10_000_000L 100 |> step 200 (BytesCopied 9_000L)
        let afterwards = before |> step 400 (BytesCopied 990_000L)

        let widths (tracker: Tracker) =
            rowsOf (Display.view width (after 1000) tracker) |> List.map (List.map (fun cell -> cell.Width, cell.Text.Length))

        Assert.Equal<(int * int) list list>(widths before, widths afterwards)

    [<Property>]
    let ``a field's width never changes with its value`` () =
        let gen =
            gen {
                let! total = Gen.choose (0, 2_000_000_000)
                let! files = Gen.choose (0, 5_000_000)
                let! copied = Gen.choose (0, 2_000_000_000)
                let! doneFiles = Gen.choose (0, 5_000_000)
                let! elapsed = Gen.choose (1, 400_000_000)
                return int64 total, files, int64 copied, doneFiles, elapsed
            }

        Prop.forAll (Arb.fromGen gen) (fun (total, files, copied, doneFiles, elapsed) ->
            let tracker =
                transferring total files
                |> step 200 (BytesCopied copied)
                |> fun t -> (Seq.fold (fun t _ -> step 300 FileTransferred t) t (Seq.truncate 5 (Seq.initInfinite id)))
                |> fun t -> { t with Shown = { Bytes = copied; Files = doneFiles } }

            rowsOf (Display.view width (after elapsed) tracker)
            |> List.forall (List.forall (fun cell -> cell.Text.Length = cell.Width)))

module ``Lightweight operations reported separately from data transfer`` =
    [<Fact>]
    let ``Backup run consisting mostly of moved files`` () =
        // Moves add no bytes and no transfers: only the one transferred file is in the totals.
        let tracker = transferring 10L 1 |> step 100 (BytesCopied 10L) |> step 200 FileTransferred
        Assert.Equal("10 B / 10 B", (rowsOf (shown tracker) |> List.head |> List.head).Text.TrimEnd())

    [<Fact>]
    let ``Lightweight operations precede byte transfer`` () =
        // The transfer phase begins at second 30, after a long metadata pass; throughput counts from there.
        let tracker =
            feed [ 0, ScanCounted 10; 30_000, TransferStarting(1000L, 1); 31_000, BytesCopied 500L ]

        Assert.Equal(500.0, Tracker.throughput (after 31_000) tracker, 6)

module ``Run summary statistics`` =
    let private result =
        { Status = SnapshotStatus.Complete
          Totals = { FilesAdded = 2; FilesChanged = 1; FilesMoved = 3; FilesDeleted = 4; FilesFailed = 1; BytesTransferred = 2048L }
          FailedPaths = [ "a\\b.txt" ] }

    [<Fact>]
    let ``Run completes with a failed file`` () =
        let text = Display.ofRun (TimeSpan.FromSeconds 3.2) result |> textLines |> String.concat "\n"
        Assert.Contains("Backup completed in 3.2s", text)
        Assert.Contains("Added:       2", text)
        Assert.Contains("Changed:     1", text)
        Assert.Contains("Moved:       3", text)
        Assert.Contains("Deleted:     4", text)
        Assert.Contains("Transferred: 2 KB", text)
        Assert.Contains("Failed (1):", text)
        Assert.Contains("    - a\\b.txt", text)

module ``Backup run outcome reflects failure severity`` =
    let private run (failed: string list) : RunResult =
        { Status = SnapshotStatus.Complete
          Totals = { FilesAdded = 0; FilesChanged = 0; FilesMoved = 0; FilesDeleted = 0; FilesFailed = List.length failed; BytesTransferred = 0L }
          FailedPaths = failed }

    let private headlineTone (display: Display) : Tone =
        match List.head display with
        | Line.Text(tone, _) -> tone
        | _ -> failwith "no headline"

    [<Fact>]
    let ``Run completes with no failed files`` () =
        let display = Display.ofRun TimeSpan.Zero (run [])
        Assert.Equal(Tone.Success, headlineTone display)
        // The detail below the headline stays unstyled.
        Assert.All(List.tail display, fun line -> match line with Line.Text(tone, _) -> Assert.Equal(Tone.Plain, tone) | _ -> ())

    [<Fact>]
    let ``Run completes with one or more failed files`` () =
        Assert.Equal(Tone.PartialFailure, headlineTone (Display.ofRun TimeSpan.Zero (run [ "x" ])))

module ``Progress bar reflects run outcome severity`` =
    let private ended (outcome: Outcome) (tracker: Tracker) : Tracker = tracker |> step 5000 (RunEnded outcome)

    [<Fact>]
    let ``Scan phase in progress`` () =
        Assert.Equal(Tone.Neutral, (barOf (shown (feed [ 0, ScanCounted 1 ]))).Tone)

    [<Fact>]
    let ``Transfer phase in progress`` () =
        Assert.Equal(Tone.PartialFailure, (barOf (shown (transferring 100L 1 |> step 200 (BytesCopied 10L)))).Tone)

    [<Fact>]
    let ``Run completes with no failed files`` () =
        let bar = barOf (shown (transferring 100L 1 |> step 200 (BytesCopied 100L) |> ended Outcome.Success))
        Assert.Equal(Tone.Success, bar.Tone)
        Assert.Equal(BarFill.Filled 100.0, bar.Fill)

    [<Fact>]
    let ``Run completes with one or more failed files`` () =
        // Only half the bytes were copied, yet the whole bar takes the outcome's colour.
        let bar = barOf (shown (transferring 100L 2 |> step 200 (BytesCopied 50L) |> ended Outcome.PartialFailure))
        Assert.Equal(Tone.PartialFailure, bar.Tone)
        Assert.Equal(BarFill.Filled 100.0, bar.Fill)

    [<Fact>]
    let ``Run terminates with a hard error`` () =
        // During the scan the indicator that was showing turns red; during the transfer the bar does.
        let scanning = barOf (shown (feed [ 0, ScanCounted 1 ] |> ended Outcome.HardError))
        Assert.Equal(Tone.Error, scanning.Tone)
        Assert.Equal(BarFill.Indeterminate, scanning.Fill)
        Assert.Equal(Tone.Error, (barOf (shown (transferring 100L 1 |> ended Outcome.HardError))).Tone)

module ``Progress display never regresses to a stale value`` =
    [<Fact>]
    let ``Out-of-order progress delivery`` () =
        // Reports of 400, 100 and 300 bytes arrive in either order: the total shown is their sum either way,
        // and no later screen shows less than an earlier one.
        let deliver (deltas: int64 list) =
            deltas
            |> List.mapi (fun index delta -> (index + 1) * 150, BytesCopied delta)
            |> List.scan (fun (previous: Tracker) (ms, event) -> step ms event previous) (transferring 800L 3)
            |> List.map (fun tracker -> tracker.Shown.Bytes)

        let ascending = deliver [ 100L; 300L; 400L ]
        let scrambled = deliver [ 400L; 100L; 300L ]
        Assert.Equal(List.last ascending, List.last scrambled)
        Assert.Equal<int64 list>(List.sort scrambled, scrambled)

    [<Property>]
    let ``the displayed byte count is non-decreasing for any delivery order`` () =
        let steps = Gen.listOf (Gen.zip (Gen.choose (0, 300)) (Gen.choose (-50, 400)))

        Prop.forAll (Arb.fromGen steps) (fun deltas ->
            let _, _, seen =
                deltas
                |> List.fold
                    (fun (tracker: Tracker, clock, seen) (gap, delta) ->
                        let clock = clock + gap
                        let next = step clock (BytesCopied(int64 delta)) tracker
                        next, clock, next.Shown.Bytes :: seen)
                    (transferring 1_000_000L 10, 0, [])

            let ordered = List.rev seen
            ordered = List.sort ordered)

module ``Incremental progress during in-flight file transfers`` =
    // The engine reports a file's bytes as they are copied; ExecutionTests and BackupRunTests cover that side.
    // Here the display's half: every report advances the figures at the next admitted redraw.
    [<Fact>]
    let ``Single large file transfer in progress`` () =
        let tracker = transferring 1000L 1 |> step 200 (BytesCopied 100L) |> step 400 (BytesCopied 100L)
        Assert.Equal(200L, tracker.Shown.Bytes)

    [<Fact>]
    let ``Two large files transferred in sequence`` () =
        let tracker =
            transferring 2000L 2
            |> step 200 (BytesCopied 500L)
            |> step 400 (BytesCopied 500L)
            |> step 600 (BytesCopied 500L)
            |> step 800 (BytesCopied 500L)

        Assert.Equal(2000L, tracker.Shown.Bytes)

    [<Fact>]
    let ``Large file placed at its mirror path via a real copy`` () =
        // Both passes report into the same file budget; the total reaches the planned bytes and no more.
        let tracker = transferring 1000L 1 |> step 200 (BytesCopied 500L) |> step 400 (BytesCopied 500L)
        Assert.Equal(1000L, tracker.Shown.Bytes)

    [<Fact>]
    let ``Target filesystem without hardlink support does not overshoot 100%`` () =
        let tracker = transferring 1000L 1 |> step 200 (BytesCopied 700L) |> step 400 (BytesCopied 700L)
        Assert.Equal(1000L, tracker.Shown.Bytes)
        Assert.Equal(100.0, Tracker.percent tracker)

module ``Progress heartbeat during stalled transfers`` =
    [<Fact>]
    let ``Transfer stalls with no new bytes reported`` () =
        let tracker = transferring 1000L 1 |> step 1000 (BytesCopied 500L)

        let rateAndEta (ms: int) =
            Tracker.throughput (after ms) tracker, Tracker.remaining (after ms) tracker |> ValueOption.get

        let r1, e1 = rateAndEta 1000
        let r2, e2 = rateAndEta 5000
        Assert.True(r2 < r1)
        Assert.True(e2 > e1)
        Assert.Equal(tracker.Shown.Bytes, 500L)

    [<Fact>]
    let ``Transfer resumes after a stall`` () =
        let stalled = transferring 1000L 1 |> step 1000 (BytesCopied 500L)
        let resumed = stalled |> step 9000 (BytesCopied 250L)
        Assert.Equal(750L, resumed.Shown.Bytes)

    [<Fact>]
    let ``Heartbeat stops once the run completes`` () =
        // Domain half: an ended tracker has no active list and its final numbers are admitted. The renderer's
        // timer stopping is tested with withProgress.
        let ended = transferring 1000L 1 |> step 1000 (BytesCopied 1000L) |> step 1001 (RunEnded Outcome.Success)
        Assert.True ended.Ended.IsSome
        Assert.Equal(1000L, ended.Shown.Bytes)

module ``File-transfer count progress`` =
    [<Fact>]
    let ``Mixed-size files are transferred`` () =
        let tracker = transferring 300L 3 |> step 200 (BytesCopied 250L) |> step 400 FileTransferred |> step 600 FileTransferred
        let first = rowsOf (shown tracker) |> List.head |> List.item 1
        Assert.Equal("Files 2 / 3", first.Text.TrimEnd())

    [<Fact>]
    let ``A content transfer fails`` () =
        // A failed file is never reported as transferred, so it stays in the total and out of the count.
        let tracker = transferring 300L 3 |> step 200 FileTransferred |> step 400 (RunEnded Outcome.PartialFailure)
        Assert.Equal(1, tracker.Shown.Files)
        Assert.Equal(3, tracker.TotalFiles)

    [<Fact>]
    let ``Plan contains only metadata operations`` () =
        let first = rowsOf (shown (transferring 0L 0)) |> List.head |> List.item 1
        Assert.Equal("Files 0 / 0", first.Text.TrimEnd())

module ``Backup transfer statistics use two aligned interactive rows`` =
    [<Fact>]
    let ``Normal interactive transfer`` () =
        let rows = rowsOf (Display.view width (after 1000) (transferring 4096L 2 |> step 200 (BytesCopied 1024L)))
        Assert.Equal(2, List.length rows)
        Assert.StartsWith("1 KB / 4 KB", (List.head rows |> List.head).Text)
        Assert.StartsWith("Files 0 / 2", (List.head rows |> List.item 1).Text)
        Assert.EndsWith("/s", (List.item 1 rows |> List.head).Text.TrimEnd())
        Assert.StartsWith("ETA", (List.item 1 rows |> List.item 1).Text)

    [<Fact>]
    let ``A displayed value changes digit count`` () =
        let before = rowsOf (shown (transferring 9_999L 9 |> step 200 (BytesCopied 9L)))
        let afterwards = rowsOf (shown (transferring 99_999L 99 |> step 200 (BytesCopied 99_000L)))

        let positions (rows: Cell list list) = rows |> List.map (List.map (fun cell -> cell.Width))
        Assert.Equal<int list list>(positions before, positions afterwards)

    [<Fact>]
    let ``Non-interactive transfer output`` () =
        let tracker = transferring 1000L 4 |> step 200 (BytesCopied 500L) |> step 400 FileTransferred
        let line = Display.plainProgress (after 1000) tracker
        Assert.Contains("500 B / 1000 B (50.0%)", line)
        Assert.Contains("Files 1 / 4", line)
        Assert.Contains("/s - ETA", line)

module ``Active file transfers listed in progress display`` =
    let private withActive (paths: string list) : Tracker =
        paths |> List.fold (fun tracker path -> step 200 (FileStarted path) tracker) (transferring 100L 10)

    let private activeOf (tracker: Tracker) : string list =
        shown tracker |> textLines

    [<Fact>]
    let ``One file being transferred`` () =
        Assert.Equal<string list>([ @"C:\s\a.txt" ], activeOf (withActive [ @"C:\s\a.txt" ]))

    [<Fact>]
    let ``Three files being transferred concurrently`` () =
        let paths = [ @"C:\s\a"; @"C:\s\b"; @"C:\s\c" ]
        Assert.Equal<string list>(paths, activeOf (withActive paths))

    [<Fact>]
    let ``More than three files being transferred concurrently`` () =
        let paths = [ @"C:\s\a"; @"C:\s\b"; @"C:\s\c"; @"C:\s\d"; @"C:\s\e" ]
        Assert.Equal<string list>([ @"C:\s\a"; @"C:\s\b"; @"C:\s\c"; "+ 2 more" ], activeOf (withActive paths))

    [<Fact>]
    let ``Short absolute path fits within budget`` () =
        let path = @"C:\" + String('x', 45)
        Assert.Equal(48, path.Length)
        Assert.Equal<string list>([ path ], activeOf (withActive [ path ]))

    [<Fact>]
    let ``Long absolute path exceeds budget`` () =
        let path = @"C:\one\two\three\four\five\six\seven\eight\nine\ten\file.txt"
        let label = activeOf (withActive [ path ]) |> List.exactlyOne
        Assert.True(label.Length <= 48)
        Assert.StartsWith(@"...\", label)
        Assert.EndsWith(@"ten\file.txt", label)

    [<Fact>]
    let ``No files currently being transferred`` () =
        let tracker = withActive [ @"C:\s\a" ] |> step 300 (FileFinished @"C:\s\a")
        Assert.Empty(activeOf tracker)

module ``Progress display updates are rate-limited`` =
    let private numbers (tracker: Tracker) : Counted = tracker.Shown

    [<Fact>]
    let ``Many small files transferred rapidly`` () =
        // A thousand one-byte files within a second: the numbers are redrawn about every 100 ms, not a thousand times.
        let mutable tracker = transferring 1000L 1000
        let mutable redraws = 0

        for index in 1..1000 do
            let next = tracker |> step index (BytesCopied 1L) |> step index FileTransferred

            if next.Shown <> tracker.Shown then
                redraws <- redraws + 1

            tracker <- next

        Assert.InRange(redraws, 5, 12)

    [<Fact>]
    let ``Active-path change does not advance numeric statistics`` () =
        let tracker = transferring 1000L 10 |> step 200 (BytesCopied 100L)
        let started = tracker |> step 210 (FileStarted @"C:\s\a") |> step 220 (BytesCopied 100L)
        // 10 ms after the last admission: the list changed, the numbers did not.
        Assert.Equal(tracker.Shown, started.Shown)
        Assert.Equal<string list>([ @"C:\s\a" ], started.Active)

    [<Fact>]
    let ``Completed file is not shown as active`` () =
        let tracker = transferring 100L 2 |> step 10 (FileStarted "a") |> step 20 (FileStarted "b") |> step 30 (FileFinished "a")
        Assert.Equal<string list>([ "b" ], tracker.Active)

    [<Fact>]
    let ``Transfer completes before a truthful refresh`` () =
        // Started and finished between two redraws: the path is simply not in the list when it is next drawn.
        let tracker = transferring 100L 1 |> step 10 (FileStarted "a") |> step 11 (FileFinished "a")
        Assert.Empty(tracker.Active)
        Assert.Empty(textLines (shown tracker))

    [<Fact>]
    let ``Progress shown at start and completion regardless of rate limit`` () =
        let started = transferring 100L 1
        Assert.True started.LastAdmitted.IsSome
        // The final update comes 1 ms after the previous one, and is shown at once.
        let finished = started |> step 200 (BytesCopied 40L) |> step 201 (BytesCopied 60L) |> step 202 (RunEnded Outcome.Success)
        Assert.Equal(100L, finished.Shown.Bytes)

    [<Property>]
    let ``admitted numeric updates are bounded by elapsed time over the interval`` () =
        let events = Gen.listOf (Gen.zip (Gen.choose (0, 60)) (Gen.choose (1, 100)))

        Prop.forAll (Arb.fromGen events) (fun gaps ->
            let begun = transferring 1_000_000L 1000
            let mutable tracker = begun
            let mutable clock = 0
            let mutable admitted = 0

            for gap, bytes in gaps do
                clock <- clock + gap
                let next = step clock (BytesCopied(int64 bytes)) tracker

                if next.LastAdmitted <> tracker.LastAdmitted then
                    admitted <- admitted + 1

                tracker <- next

            let bound = int (float clock / interval.TotalMilliseconds) + 1
            admitted <= bound)

module ``Progress delivery completes before display teardown`` =
    [<Fact>]
    let ``Final progress report delivered asynchronously`` () =
        // A report that arrives after the end event cannot change what the final screen shows as active, and the
        // ended tracker is the last word: the bracket in the CLI tests drains reports before teardown.
        let ended = transferring 100L 1 |> step 10 (BytesCopied 100L) |> step 20 (RunEnded Outcome.Success)
        let late = ended |> step 30 (FileStarted "late")
        let finalLines = shown late |> lines
        Assert.DoesNotContain("late", String.concat "\n" finalLines)

module ``ETA is never negative`` =
    [<Property>]
    let ``ETA is never negative`` () =
        let gen = Gen.zip3 (Gen.choose (0, 1_000_000)) (Gen.choose (0, 1_000_000)) (Gen.choose (-5000, 600_000))

        Prop.forAll (Arb.fromGen gen) (fun (total, copied, elapsed) ->
            let tracker = transferring (int64 total) 1 |> step 200 (BytesCopied(int64 copied))

            match Tracker.remaining (after elapsed) tracker with
            | ValueSome left -> left >= TimeSpan.Zero
            | ValueNone -> true)
