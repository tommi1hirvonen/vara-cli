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
    /// The colours of the kinds of change a snapshot records, shared by every view that shows one.
    | Added
    | Changed
    | Moved
    | Deleted
    | Linked

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

/// A run of text in one tone, in a line that mixes tones.
type Span = { Tone: Tone; Text: string }

[<RequireQualifiedAccess>]
type Align =
    | Left
    | Right

type Column = { Header: string; Align: Align }

/// An aligned table with column headers; every row has one span per column. The renderer draws its border.
type Table = { Columns: Column list; Rows: Span list list }

[<RequireQualifiedAccess>]
type Line =
    | Bar of bar: BarRow
    | Row of cells: Cell list
    | Text of tone: Tone * text: string
    /// One line made of spans in different tones.
    | Spans of spans: Span list
    | Table of table: Table
    /// A titled frame around other lines, drawn with rounded corners.
    | Box of title: string * lines: Line list

/// What a screen shows, as plain data: the interactive renderer draws it, the plain renderer prints it.
type Display = Line list

module ByteSize =
    let private units = [| "B"; "KB"; "MB"; "GB"; "TB" |]

    [<TailCall>]
    let rec private scale (value: float) (unit: int) : struct (float * int) =
        if value >= 1024.0 && unit < units.Length - 1 then scale (value / 1024.0) (unit + 1) else struct (value, unit)

    /// "512 B", "1.5 KB", "2 GB": two decimals at most, binary units.
    let format (bytes: int64) : string =
        match scale (float bytes) 0 with
        | struct (_, 0) -> bytes.ToString CultureInfo.InvariantCulture + " B"
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

    /// The two labels a progress screen shows: during the scan, and during the transfer. Each starts with a space.
    /// The basis says what the percentage and the ETA are measured in.
    type Wording =
        { Scanning: string
          Transferring: string
          Basis: Basis }

    /// What the backup says.
    let backupWording: Wording =
        { Scanning = scanLabel
          Transferring = transferLabel
          Basis = Basis.Bytes }

    /// What a prune says: it starts by evaluating, then counts the blobs it removes. The labels are the C# build's.
    let pruneWording: Wording =
        { Scanning = " Evaluating..."
          Transferring = " Deleting blobs"
          Basis = Basis.Items }

    /// What a full check says: the percentage is weighted by the manifest sizes of the blobs.
    let checkWording: Wording =
        { Scanning = " Enumerating..."
          Transferring = " Checking blobs"
          Basis = Basis.Bytes }

    /// What a quick check says: it only looks for blobs, so the percentage counts them.
    let quickCheckWording: Wording = { checkWording with Basis = Basis.Items }
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
    let viewWith (wording: Wording) (width: int) (now: DateTimeOffset) (tracker: Tracker) : Display =
        match tracker.Phase with
        | Phase.Scanning _ ->
            let tone = tracker.Ended |> ValueOption.map toneOf |> ValueOption.defaultValue Tone.Neutral
            [ bar width wording.Scanning BarFill.Indeterminate "" tone ]
        | Phase.Transferring ->
            let percent, tone =
                match tracker.Ended with
                | ValueSome outcome -> 100.0, toneOf outcome
                | ValueNone -> Tracker.percentBy wording.Basis tracker, Tone.PartialFailure

            let rate = Tracker.throughput now tracker |> int64
            let eta = "ETA " + etaText (Tracker.remainingBy wording.Basis now tracker)
            let barLine = bar width wording.Transferring (BarFill.Filled percent) (percentText percent) tone
            let files = $"Files {tracker.Shown.Files} / {tracker.TotalFiles}"

            match wording.Basis with
            | Basis.Bytes ->
                [ barLine
                  Line.Row
                      [ cell leftWidth (ratio (tracker.Shown.Bytes + tracker.ShownCredited) tracker.TotalBytes)
                        cell rightWidth files ]
                  Line.Row [ cell leftWidth (ByteSize.format rate + "/s"); cell rightWidth eta ] ]
                @ if tracker.Ended.IsSome then [] else activeLines tracker
            | Basis.Items ->
                // Items have no byte count or throughput: the count and the ETA share the one row.
                [ barLine; Line.Row [ cell leftWidth files; cell rightWidth eta ] ]
                @ if tracker.Ended.IsSome then [] else activeLines tracker

    /// The backup's progress screen.
    let view (width: int) (now: DateTimeOffset) (tracker: Tracker) : Display = viewWith backupWording width now tracker

    /// One appended line of progress, for output that cannot redraw in place. A byte-weighted run shows the bytes, the
    /// count and the throughput; a run measured in items shows the count, the percentage and the ETA.
    let plainProgressWith (basis: Basis) (now: DateTimeOffset) (tracker: Tracker) : string =
        let rate = Tracker.throughput now tracker |> int64
        let percent = (Tracker.percentBy basis tracker).ToString("0.0", CultureInfo.InvariantCulture)
        let eta = etaText (Tracker.remainingBy basis now tracker)

        match basis with
        | Basis.Bytes ->
            $"{ratio (tracker.Shown.Bytes + tracker.ShownCredited) tracker.TotalBytes} ({percent}%%) - Files {tracker.Shown.Files} / {tracker.TotalFiles} - {ByteSize.format rate}/s - ETA {eta}"
        | Basis.Items -> $"Files {tracker.Shown.Files} / {tracker.TotalFiles} ({percent}%%) - ETA {eta}"

    /// The backup's line of plain progress.
    let plainProgress (now: DateTimeOffset) (tracker: Tracker) : string = plainProgressWith Basis.Bytes now tracker

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

    /// The characters a border is drawn with.
    type Glyphs =
        { TopLeft: string
          TopRight: string
          BottomLeft: string
          BottomRight: string
          Horizontal: string
          Vertical: string
          TeeDown: string
          TeeUp: string
          TeeLeft: string
          TeeRight: string
          Cross: string }

    /// The rounded border of the live renderer, so that a table or a box reads the same in the live region and in plain output
    /// (cli-presentation: "Bordered widgets use a consistent rounded-corner style").
    let roundedGlyphs: Glyphs =
        { TopLeft = "╭"
          TopRight = "╮"
          BottomLeft = "╰"
          BottomRight = "╯"
          Horizontal = "─"
          Vertical = "│"
          TeeDown = "┬"
          TeeUp = "┴"
          TeeLeft = "┤"
          TeeRight = "├"
          Cross = "┼" }

    /// What a terminal that cannot show the box-drawing characters gets instead: the same shape in ASCII.
    let asciiGlyphs: Glyphs =
        { TopLeft = "+"
          TopRight = "+"
          BottomLeft = "+"
          BottomRight = "+"
          Horizontal = "-"
          Vertical = "|"
          TeeDown = "+"
          TeeUp = "+"
          TeeLeft = "+"
          TeeRight = "+"
          Cross = "+" }

    let private pad (align: Align) (width: int) (text: string) : string =
        match align with
        | Align.Left -> text.PadRight width
        | Align.Right -> text.PadLeft width

    /// The text of a table, drawn with a border: a header row, a rule, then the rows. Each column is as wide as its
    /// widest cell, and a right-aligned column lines its digits up.
    let tableLinesWith (glyphs: Glyphs) (table: Table) : string list =
        let cells = table.Rows |> List.map (List.map _.Text)

        let widths =
            table.Columns
            |> List.mapi (fun index column ->
                cells
                |> List.fold (fun width row -> max width (List.tryItem index row |> Option.map String.length |> Option.defaultValue 0)) column.Header.Length)

        let rule (left: string) (middle: string) (right: string) : string =
            left + (widths |> List.map (fun width -> String.replicate (width + 2) glyphs.Horizontal) |> String.concat middle) + right

        let line (texts: string list) : string =
            glyphs.Vertical
            + (List.zip3 table.Columns widths texts
               |> List.map (fun (column, width, text) -> " " + pad column.Align width text + " ")
               |> String.concat glyphs.Vertical)
            + glyphs.Vertical

        [ rule glyphs.TopLeft glyphs.TeeDown glyphs.TopRight
          line (table.Columns |> List.map _.Header)
          rule glyphs.TeeRight glyphs.Cross glyphs.TeeLeft ]
        @ (cells |> List.map (fun row -> line (table.Columns |> List.mapi (fun index _ -> List.tryItem index row |> Option.defaultValue ""))))
        @ [ rule glyphs.BottomLeft glyphs.TeeUp glyphs.BottomRight ]

    /// The text of a box: the lines inside it between borders, under the title.
    let boxLinesWith (glyphs: Glyphs) (title: string) (inner: string list) : string list =
        let width = inner |> List.fold (fun width text -> max width text.Length) (title.Length + 2)

        let heading =
            if title.Length = 0 then
                String.replicate (width + 2) glyphs.Horizontal
            else
                glyphs.Horizontal + " " + title + " " + String.replicate (width - title.Length - 1) glyphs.Horizontal

        [ glyphs.TopLeft + heading + glyphs.TopRight ]
        @ (inner |> List.map (fun text -> glyphs.Vertical + " " + text.PadRight width + " " + glyphs.Vertical))
        @ [ glyphs.BottomLeft + String.replicate (width + 2) glyphs.Horizontal + glyphs.BottomRight ]

    /// The unstyled text of a display, one string per output line, its borders drawn with the given glyphs.
    let rec plainLinesWith (glyphs: Glyphs) (display: Display) : string list =
        display
        |> List.collect (fun line ->
            match line with
            | Line.Text(_, text) -> [ text ]
            | Line.Row cells -> [ (cells |> List.map _.Text |> String.concat "").TrimEnd() ]
            | Line.Bar bar -> [ (bar.Label + bar.Trailing).TrimEnd() ]
            | Line.Spans spans -> [ spans |> List.map _.Text |> String.concat "" ]
            | Line.Table table -> tableLinesWith glyphs table
            | Line.Box(title, lines) -> boxLinesWith glyphs title (plainLinesWith glyphs lines))

    /// The unstyled text of a display with rounded borders: what tests read.
    let plainLines (display: Display) : string list = plainLinesWith roundedGlyphs display

    /// The text with the characters a legacy console cannot show replaced by ASCII: the guides of a tree, and the
    /// arrow of a picker entry.
    let toAscii (text: string) : string =
        text.Replace("├──", "|--").Replace("└──", "`--").Replace("│", "|").Replace("→", ">")

    /// The display with the function applied to every text in it: what a console that cannot show Unicode is given.
    let rec mapTexts (change: string -> string) (display: Display) : Display =
        display
        |> List.map (fun line ->
            match line with
            | Line.Bar _
            | Line.Row _ -> line
            | Line.Text(tone, text) -> Line.Text(tone, change text)
            | Line.Spans spans -> Line.Spans(spans |> List.map (fun span -> { span with Text = change span.Text }))
            | Line.Table table ->
                Line.Table
                    { table with
                        Rows = table.Rows |> List.map (List.map (fun span -> { span with Text = change span.Text })) }
            | Line.Box(title, lines) -> Line.Box(change title, mapTexts change lines))
