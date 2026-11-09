/// Interprets a `Display` in Spectre.Console's live region, redrawn in place. The one module that draws a live
/// display; everything it draws comes from `Display.view`.
module Vara.Cli.LiveRenderer

open System
open System.Threading.Tasks
open Spectre.Console
open Spectre.Console.Rendering
open Vara.Domain

let private barGlyph = '━'
let private remainingStyle = Style Color.Grey
/// How long the moving segment of an indeterminate bar is.
let private pulseLength = 8

/// One bar line drawn from characters of a single style each, so its length is exactly `Width`.
let private barLine (frame: int) (bar: BarRow) : IRenderable =
    let tone = Terminal.styleOfTone bar.Tone
    let line = Paragraph()
    line.Append(bar.Label, Style.Plain) |> ignore

    let segment (length: int) (style: Style) =
        line.Append(String(barGlyph, max 0 length), style) |> ignore

    match bar.Fill with
    | BarFill.Filled percent ->
        let filled = int (Math.Round(float bar.Width * percent / 100.0))
        segment filled tone
        segment (bar.Width - filled) remainingStyle
    | BarFill.Indeterminate ->
        // A segment that travels along the bar, one step per frame.
        let length = min pulseLength bar.Width
        let position = frame % (bar.Width + length) - length
        let from = max 0 position
        let until = min bar.Width (position + length)
        segment from remainingStyle
        segment (until - from) tone
        segment (bar.Width - until) remainingStyle

    line.Append(bar.Trailing, Style.Plain) |> ignore
    line :> IRenderable

/// A line of spans, each in its own style.
let private spansLine (spans: Span list) : IRenderable =
    let line = Paragraph()

    for span in spans do
        line.Append(span.Text, Terminal.styleOfTone span.Tone) |> ignore

    line :> IRenderable

/// A table with the rounded border every bordered widget uses, its numeric columns right-aligned.
let private tableOf (table: Table) : IRenderable =
    let widget = Spectre.Console.Table().Border TableBorder.Rounded

    for column in table.Columns do
        let spectreColumn = TableColumn(Text column.Header)

        match column.Align with
        | Align.Right -> spectreColumn.RightAligned() |> ignore
        | Align.Left -> ()

        widget.AddColumn spectreColumn |> ignore

    for row in table.Rows do
        widget.AddRow(row |> List.map (fun span -> Text(span.Text, Terminal.styleOfTone span.Tone) :> IRenderable) |> Array.ofList)
        |> ignore

    widget :> IRenderable

let rec private renderLine (frame: int) (line: Line) : IRenderable =
    match line with
    | Line.Bar bar -> barLine frame bar
    | Line.Row cells -> Paragraph(cells |> List.map _.Text |> String.concat "", Style.Plain) :> IRenderable
    | Line.Text(tone, text) -> Text(text, Terminal.styleOfTone tone) :> IRenderable
    | Line.Spans spans -> spansLine spans
    | Line.Table table -> tableOf table
    | Line.Box(title, lines) ->
        let panel = Panel(Rows(lines |> List.map (renderLine frame)))
        panel.Border <- BoxBorder.Rounded
        if title.Length > 0 then panel.Header <- PanelHeader title
        panel :> IRenderable

let private render (frame: int) (display: Display) : IRenderable =
    Rows(display |> List.map (renderLine frame)) :> IRenderable

/// Prints a display once, in colour, below what is already on the console. A table or a box ends its own line; any
/// other widget is given a line break.
let print (console: IAnsiConsole) (display: Display) : unit =
    let shown = if console.Profile.Capabilities.Unicode then display else Display.mapTexts Display.toAscii display

    for line in shown do
        console.Write(renderLine 0 line)

        match line with
        | Line.Table _
        | Line.Box _ -> ()
        | Line.Bar _
        | Line.Row _
        | Line.Text _
        | Line.Spans _ -> console.WriteLine()

/// The columns a bar line may use: one fewer than the console's, so a full line never wraps.
let width (console: IAnsiConsole) : int = console.Profile.Width - 1

/// Writes a terminal control sequence straight to the console's output, bypassing any markup or wrapping.
let writeSequence (console: IAnsiConsole) (sequence: string) : unit =
    let writer = console.Profile.Out.Writer
    writer.Write sequence
    writer.Flush()

/// The calls of a full-screen session: the alternate screen buffer keeps the user's terminal history and scrollback
/// untouched, and is cleared before each screen so that no earlier copy of one remains.
type AlternateScreen =
    { Enter: unit -> unit
      Clear: unit -> unit
      /// Back to the primary screen buffer, with the cursor shown again.
      Leave: unit -> unit }

/// The alternate screen of a console. The control sequences are written here and nowhere else: the alternate buffer on
/// (`?1049h`), and off with the cursor shown again (`?1049l`, `?25h`), as the C# build's `RestorePrimaryScreenBuffer` does.
let alternateScreen (console: IAnsiConsole) : AlternateScreen =
    { Enter = fun () -> writeSequence console "[?1049h"
      Clear = fun () -> console.Clear()
      Leave = fun () -> writeSequence console "[?1049l[?25h" }

/// Runs `body` with a live region and the function that redraws it. The region is left on screen when the body ends,
/// so the last bar stays above the summary. `draw` is not thread-safe: the caller serializes its calls.
let run (console: IAnsiConsole) (body: (Display -> unit) -> Task<'a>) : Task<'a> =
    let live = console.Live(Text "")
    live.AutoClear <- false

    live.StartAsync(
        Func<LiveDisplayContext, Task<'a>>(fun context ->
            let frame = ref 0

            let draw (display: Display) : unit =
                frame.Value <- frame.Value + 1
                context.UpdateTarget(render frame.Value display)
                context.Refresh()

            body draw)
    )
