/// Interprets a `Display` in Spectre.Console's live region, redrawn in place. The one module that draws a live
/// display; everything it draws comes from `Display.view`.
module Vara.Cli.LiveRenderer

open System
open System.Threading.Tasks
open Spectre.Console
open Spectre.Console.Rendering
open Vara.Domain

let private barGlyph = '━'
let private remainingStyle = Style(Color.Grey)
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

let private renderLine (frame: int) (line: Line) : IRenderable =
    match line with
    | Line.Bar bar -> barLine frame bar
    | Line.Row cells -> Paragraph(cells |> List.map (fun cell -> cell.Text) |> String.concat "", Style.Plain) :> IRenderable
    | Line.Text(tone, text) -> Text(text, Terminal.styleOfTone tone) :> IRenderable

let private render (frame: int) (display: Display) : IRenderable =
    Rows(display |> List.map (renderLine frame)) :> IRenderable

/// The columns a bar line may use: one fewer than the console's, so a full line never wraps.
let width (console: IAnsiConsole) : int = console.Profile.Width - 1

/// Writes a terminal control sequence straight to the console's output, bypassing any markup or wrapping.
let writeSequence (console: IAnsiConsole) (sequence: string) : unit =
    let writer = console.Profile.Out.Writer
    writer.Write sequence
    writer.Flush()

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
