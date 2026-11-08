namespace Vara.Domain

open System
open System.Globalization

/// How a piece of text is styled. The renderers map each role to a colour; nothing here knows a colour.
[<RequireQualifiedAccess>]
type Tone =
    /// The default, unstyled text.
    | Plain
    /// Secondary text, such as a stack trace.
    | Detail
    /// Work in progress with no outcome yet, such as the scan indicator.
    | Neutral
    | Success
    /// Also the colour of a transfer bar that has not reached its outcome.
    | PartialFailure
    | Error

[<RequireQualifiedAccess>]
type BarFill =
    | Indeterminate
    | Filled of percent: float

/// A field of a row. Its text is already exactly `Width` characters, so a changing value never moves its neighbours.
type Cell = { Width: int; Text: string }

/// A progress bar on a line of its own: a label, the bar itself at `Width`, and a trailing field.
type BarRow =
    { Label: string
      Width: int
      Fill: BarFill
      Trailing: string
      Tone: Tone }

[<RequireQualifiedAccess>]
type Line =
    | Bar of bar: BarRow
    | Row of cells: Cell list
    | Text of tone: Tone * text: string

/// What a screen shows, as plain data: the interactive renderer draws it, the plain renderer prints it.
type Display = Line list

module ByteSize =
    let private units = [| "B"; "KB"; "MB"; "GB"; "TB" |]

    let rec private scale (value: float) (unit: int) : struct (float * int) =
        if value >= 1024.0 && unit < units.Length - 1 then scale (value / 1024.0) (unit + 1) else struct (value, unit)

    /// "512 B", "1.5 KB", "2 GB": two decimals at most, binary units.
    let format (bytes: int64) : string =
        match scale (float bytes) 0 with
        | struct (_, 0) -> bytes.ToString(CultureInfo.InvariantCulture) + " B"
        | struct (value, unit) -> value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit]

module Elapsed =
    /// "2h 3m 4s", "3m 4s", "3.2s".
    let format (duration: TimeSpan) : string =
        if duration.TotalHours >= 1.0 then
            $"{int duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s"
        elif duration.TotalMinutes >= 1.0 then
            $"{duration.Minutes}m {duration.Seconds}s"
        else
            duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s"

module Display =
    // The widths below come from the C# build's layout; the spec fixes the behaviour (fields keep their position)
    // and not the numbers, so they are kept as they were.
    let private scanLabel = " Scanning files..."
    let private transferLabel = " Backing up..."
    /// The label column is as wide as the longer label, so the bar is the same length in both phases.
    let labelWidth = max scanLabel.Length transferLabel.Length
    /// " 100.0%": the percentage field is five characters wide, so the trailing column never changes width.
    let private percentText (percent: float) : string =
        " " + percent.ToString("0.0", CultureInfo.InvariantCulture).PadLeft 5 + "%"

    let trailingWidth = (percentText 0.0).Length
    /// The two statistics columns: bytes and throughput on the left, files and ETA on the right.
    let private leftWidth = 32
    let private rightWidth = 28
    /// Active transfers: how many are listed, and how long each path label may be.
    let private activeShown = 3
    let private activePathBudget = 48

    /// The tone each outcome is shown in (cli-presentation: "Outcome severity is visually distinct").
    let toneOf (outcome: Outcome) : Tone =
        match outcome with
        | Outcome.Success -> Tone.Success
        | Outcome.PartialFailure -> Tone.PartialFailure
        | Outcome.HardError -> Tone.Error

    /// A cell of exactly `width` characters: padded on the right, or cut when the text is longer.
    let private cell (width: int) (text: string) : Cell =
        { Width = width
          Text = if text.Length > width then text.Substring(0, width) else text.PadRight width }

    let private barWidth (width: int) : int = max 1 (width - labelWidth - trailingWidth)

    let private bar (width: int) (label: string) (fill: BarFill) (trailing: string) (tone: Tone) : Line =
        Line.Bar
            { Label = label.PadRight labelWidth
              Width = barWidth width
              Fill = fill
              Trailing = trailing.PadRight trailingWidth
              Tone = tone }

    let private ratio (shown: int64) (total: int64) : string = ByteSize.format shown + " / " + ByteSize.format total

    let private etaText (remaining: TimeSpan voption) : string =
        match remaining with
        | ValueSome left -> Elapsed.format left
        | ValueNone -> "calculating..."

    let private activeLines (tracker: Tracker) : Line list =
        let paths = List.rev tracker.Active
        let shown = paths |> List.truncate activeShown |> List.map (fun path -> Line.Text(Tone.Plain, PathLabel.truncate activePathBudget path))
        let more = List.length paths - activeShown

        if more > 0 then shown @ [ Line.Text(Tone.Plain, $"+ {more} more") ] else shown

    /// The progress screen for a terminal `width` wide at clock value `now`. The numbers are the last admitted ones;
    /// throughput and ETA come from `now`, so a later value with no new bytes shows a stall.
    let view (width: int) (now: DateTimeOffset) (tracker: Tracker) : Display =
        match tracker.Phase with
        | Phase.Scanning _ ->
            let tone = tracker.Ended |> ValueOption.map toneOf |> ValueOption.defaultValue Tone.Neutral
            [ bar width scanLabel BarFill.Indeterminate "" tone ]
        | Phase.Transferring ->
            let percent, tone =
                match tracker.Ended with
                | ValueSome outcome -> 100.0, toneOf outcome
                | ValueNone -> Tracker.percent tracker, Tone.PartialFailure

            let rate = Tracker.throughput now tracker |> int64

            [ bar width transferLabel (BarFill.Filled percent) (percentText percent) tone
              Line.Row
                  [ cell leftWidth (ratio tracker.Shown.Bytes tracker.TotalBytes)
                    cell rightWidth $"Files {tracker.Shown.Files} / {tracker.TotalFiles}" ]
              Line.Row
                  [ cell leftWidth (ByteSize.format rate + "/s")
                    cell rightWidth ("ETA " + etaText (Tracker.remaining now tracker)) ] ]
            @ (if tracker.Ended.IsSome then [] else activeLines tracker)

    /// One appended line of progress, for output that cannot redraw in place.
    let plainProgress (now: DateTimeOffset) (tracker: Tracker) : string =
        let rate = Tracker.throughput now tracker |> int64

        let percent = (Tracker.percent tracker).ToString("0.0", CultureInfo.InvariantCulture)

        $"{ratio tracker.Shown.Bytes tracker.TotalBytes} ({percent}%%) - Files {tracker.Shown.Files} / {tracker.TotalFiles} - {ByteSize.format rate}/s - ETA {etaText (Tracker.remaining now tracker)}"

    let private failedLines (paths: string list) : Line list =
        match paths with
        | [] -> []
        | _ ->
            Line.Text(Tone.Plain, $"  Failed ({List.length paths}):")
            :: (paths |> List.map (fun path -> Line.Text(Tone.Plain, $"    - {path}")))

    let private count (label: string) (value: int) : Line = Line.Text(Tone.Plain, $"  {label}{value}")

    /// The summary of a run that was not refused: the headline in the outcome's tone, the detail unstyled.
    let ofRun (elapsed: TimeSpan) (result: RunResult) : Display =
        let outcome = Run.outcome result

        let headline =
            match result.Status with
            | SnapshotStatus.Cancelled -> "Backup cancelled after " + Elapsed.format elapsed
            | SnapshotStatus.Running
            | SnapshotStatus.Complete
            | SnapshotStatus.Failed -> "Backup completed in " + Elapsed.format elapsed

        [ Line.Text(toneOf outcome, headline)
          count "Added:       " result.Totals.FilesAdded
          count "Changed:     " result.Totals.FilesChanged
          count "Moved:       " result.Totals.FilesMoved
          count "Deleted:     " result.Totals.FilesDeleted
          Line.Text(Tone.Plain, "  Transferred: " + ByteSize.format result.Totals.BytesTransferred) ]
        @ failedLines result.FailedPaths

    /// How a dry run ends the command: paths the scan could not read make it a partial failure.
    let planOutcome (summary: PlanSummary) : Outcome =
        if List.isEmpty summary.Unscanned then Outcome.Success else Outcome.PartialFailure

    /// The summary of a dry run: what a real run would do and what it could not scan.
    let ofPlan (summary: PlanSummary) : Display =
        [ Line.Text(toneOf (planOutcome summary), "Dry run - no changes were made")
          count "Added:       " summary.Added
          count "Changed:     " summary.Changed
          count "Moved:       " summary.Moved
          count "Deleted:     " summary.Deleted
          Line.Text(Tone.Plain, "  To transfer: " + ByteSize.format summary.PlannedBytes) ]
        @ failedLines summary.Unscanned
