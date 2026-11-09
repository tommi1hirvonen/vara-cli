/// Tests of the presentation conventions every command shares. Each module is named after a requirement of
/// openspec/specs/cli-presentation/spec.md or progress-reporting/spec.md and each test after one of its scenarios.
module Vara.Domain.Tests.PresentationTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain

module ``Outcome severity is visually distinct`` =
    [<Fact>]
    let ``Command completes with no failures`` () =
        Assert.Equal(Tone.Success, Display.toneOf Outcome.Success)

    [<Fact>]
    let ``Command completes with partial failures`` () =
        Assert.Equal(Tone.PartialFailure, Display.toneOf Outcome.PartialFailure)

    [<Fact>]
    let ``Command reports a hard error`` () =
        Assert.Equal(Tone.Error, Display.toneOf Outcome.HardError)

    [<Fact>]
    let ``Outcome report includes multi-line supporting detail`` () =
        let summary = Display.ofPlan { Added = 2; Changed = 0; Moved = 0; Deleted = 0; PlannedBytes = 10L; Unscanned = [ "x" ] }

        match summary with
        | Line.Text(headline, _) :: detail ->
            Assert.Equal(Tone.PartialFailure, headline)

            for line in detail do
                match line with
                | Line.Text(tone, _) -> Assert.Equal(Tone.Plain, tone)
                | Line.Bar _
                | Line.Row _
                | Line.Spans _
                | Line.Table _
                | Line.Box _ -> failwith "a summary has only text lines"
        | _ -> failwith "no headline"

    [<Fact>]
    let ``the three tones differ`` () =
        let tones = [ Outcome.Success; Outcome.PartialFailure; Outcome.HardError ] |> List.map Display.toneOf
        Assert.Equal(3, tones |> List.distinct |> List.length)

module ``Outcome severity determines process exit code`` =
    [<Fact>]
    let ``Command completes with no failures`` () = Assert.Equal(0, Outcome.exitCode Outcome.Success)

    [<Fact>]
    let ``Command completes with partial failures`` () =
        let code = Outcome.exitCode Outcome.PartialFailure
        Assert.NotEqual(0, code)
        Assert.NotEqual(Outcome.exitCode Outcome.HardError, code)

    [<Fact>]
    let ``Command reports a hard error`` () = Assert.Equal(1, Outcome.exitCode Outcome.HardError)

module ``Non-interactive or color-incapable output falls back to plain text`` =
    let private capable = { IsTerminal = true; SupportsColor = true; NoColorRequested = false; InputIsInteractive = true }

    [<Fact>]
    let ``Output redirected to a file or pipe`` () =
        Assert.Equal(OutputMode.Plain, OutputMode.choose false { capable with IsTerminal = false })

    [<Fact>]
    let ``NO_COLOR is set`` () =
        Assert.Equal(OutputMode.Plain, OutputMode.choose false { capable with NoColorRequested = true })
        // Even redirected, the layout still applies; only the mode is plain.
        Assert.Equal(OutputMode.Plain, OutputMode.choose false { capable with NoColorRequested = true; IsTerminal = false })

    [<Fact>]
    let ``Terminal does not support color`` () =
        Assert.Equal(OutputMode.Plain, OutputMode.choose false { capable with SupportsColor = false })

    [<Fact>]
    let ``an interactive colour terminal gets the live display and json wins over all`` () =
        Assert.Equal(OutputMode.Live, OutputMode.choose false capable)
        Assert.Equal(OutputMode.Json, OutputMode.choose true capable)
        Assert.Equal(OutputMode.Json, OutputMode.choose true { capable with IsTerminal = false })

module ``Windows Terminal progress indicator`` =
    let private feed (events: ProgressEvent list) : Tracker =
        let now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        events |> List.fold (fun tracker event -> Tracker.update Tracker.redrawInterval now event tracker) Tracker.initial

    [<Fact>]
    let ``Indeterminate phase`` () =
        Assert.Equal(Indicator.Indeterminate, TerminalProgress.indicator (feed [ ScanCounted 5 ]))
        Assert.Equal("\u001b]9;4;3;0\u0007", TerminalProgress.sequence Indicator.Indeterminate)

    [<Fact>]
    let ``Determinate phase`` () =
        let tracker = feed [ TransferStarting(200L, 1); BytesCopied 50L ]
        // Redraws are rate-limited, so the first admitted update (no bytes yet) is what shows.
        let tracker = { tracker with Shown = { tracker.Shown with Bytes = 50L } }
        Assert.Equal(Indicator.Percentage 25, TerminalProgress.indicator tracker)
        Assert.Equal("\u001b]9;4;1;25\u0007", TerminalProgress.sequence (Indicator.Percentage 25))

    [<Fact>]
    let ``Command completes or stops`` () =
        let ended = feed [ TransferStarting(200L, 1); RunEnded Outcome.HardError ]
        Assert.Equal(Indicator.Hidden, TerminalProgress.indicator ended)
        Assert.Equal("\u001b]9;4;0;0\u0007", TerminalProgress.sequence Indicator.Hidden)

    [<Property>]
    let ``the percentage is bounded from 0 to 100`` () =
        let gen = Gen.zip (Gen.choose (0, 100_000)) (Gen.choose (0, 200_000))

        Prop.forAll (Arb.fromGen gen) (fun (total, copied) ->
            let tracker =
                feed [ TransferStarting(int64 total, 1) ]
                |> fun tracker -> { tracker with Shown = { tracker.Shown with Bytes = int64 copied } }

            match TerminalProgress.indicator tracker with
            | Indicator.Percentage percent -> percent >= 0 && percent <= 100
            | Indicator.Hidden
            | Indicator.Indeterminate -> false)

module ``Terminal progress compatibility`` =
    [<Fact>]
    let ``Progress in a supported terminal`` () =
        Assert.True(TerminalProgress.enabled OutputMode.Live "a-guid")

    [<Fact>]
    let ``Redirected or machine-readable output`` () =
        Assert.False(TerminalProgress.enabled OutputMode.Plain "a-guid")
        Assert.False(TerminalProgress.enabled OutputMode.Json "a-guid")

    [<Fact>]
    let ``Another terminal host`` () =
        Assert.False(TerminalProgress.enabled OutputMode.Live null)
        Assert.False(TerminalProgress.enabled OutputMode.Live "  ")

module ``Machine-readable JSON output for the backup command`` =
    let private at (hour: int) = DateTimeOffset(2026, 1, 1, hour, 0, 0, TimeSpan.Zero)

    let private members (value: JsonValue) : Map<string, JsonValue> =
        match value with
        | JsonValue.Object members -> Map.ofList members
        | _ -> failwith "not an object"

    [<Fact>]
    let ``JSON output for a completed real run`` () =
        let result =
            { Status = SnapshotStatus.Cancelled
              Totals = { FilesAdded = 1; FilesChanged = 2; FilesMoved = 3; FilesDeleted = 4; FilesFailed = 5; BytesTransferred = 99L }
              FailedPaths = [ "a"; "b" ] }

        let json = RunJson.ofRun "docs" (at 1) (at 2) result
        let fields = members json
        Assert.Equal(JsonValue.String "executed", fields["mode"])
        Assert.Equal(JsonValue.Bool true, fields["cancelled"])
        Assert.Equal(JsonValue.Number 99L, fields["bytesTransferred"])
        Assert.Equal(JsonValue.Number 5L, fields["failed"])
        Assert.Equal(JsonValue.Array [ JsonValue.String "a"; JsonValue.String "b" ], fields["failedPaths"])

        match json with
        | JsonValue.Object pairs ->
            Assert.Equal<string list>(
                [ "mode"; "profile"; "startedAt"; "completedAt"; "cancelled"; "added"; "changed"; "moved"; "deleted"; "failed"; "failedPaths"; "bytesTransferred" ],
                pairs |> List.map fst)
        | _ -> failwith "not an object"

    [<Fact>]
    let ``JSON output for a dry run`` () =
        let summary = { Added = 3; Changed = 2; Moved = 1; Deleted = 0; PlannedBytes = 1234L; Unscanned = [ "u" ] }
        let fields = members (RunJson.ofPlan "docs" (at 1) (at 2) summary)
        Assert.Equal(JsonValue.String "dry-run", fields["mode"])
        Assert.Equal(JsonValue.Bool false, fields["cancelled"])
        Assert.Equal(JsonValue.Number 1234L, fields["bytesTransferred"])
        Assert.Equal(JsonValue.Number 1L, fields["failed"])

module ``Active file transfers listed in progress display path labels`` =
    [<Fact>]
    let ``a path that fits is unchanged and one that does not keeps its filename`` () =
        Assert.Equal(@"C:\a\b.txt", PathLabel.truncate 48 @"C:\a\b.txt")
        Assert.Equal(@"...\y\file.txt", PathLabel.truncate 14 @"C:\x\y\file.txt")
        Assert.Equal("...mething.txt", PathLabel.truncate 14 @"C:\x\averyveryverylongsomething.txt")
        Assert.Equal("", PathLabel.truncate 0 "abc")

    [<Property>]
    let ``a label never exceeds its budget`` () =
        let segment = Gen.elements [ "a"; "bb"; "ccc"; "dddddddd"; "eeeeeeeeeeeeeeeeeeeeeee" ]

        let paths =
            Gen.listOfLength 5 segment |> Gen.map (fun parts -> @"C:\" + String.concat @"\" parts)

        Prop.forAll (Arb.fromGen (Gen.zip paths (Gen.choose (1, 60)))) (fun (path, budget) ->
            (PathLabel.truncate budget path).Length <= budget)
