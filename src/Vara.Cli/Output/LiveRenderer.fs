/// Interprets a `Display` in Spectre.Console's live region, redrawn in place. The one module that draws a live
/// display; everything it draws comes from `Display.view`.
module Vara.Cli.LiveRenderer

open System
open System.Threading.Tasks
open Spectre.Console
open Spectre.Console.Rendering
open Vara.Domain

let private remainingStyle = Style Color.Grey

/// The bar of a bar line: Spectre's own `ProgressBarColumn`, rendered for a task of its own. An indeterminate fill is a
/// task with no known total, so the pulse animates itself from the clock at each redraw; a filled bar is a task at its
/// percentage of 100, completed and finished in the row's tone, as the C# build draws it. The column is the only public
/// way to reach the native widget.
let private nativeBar (bar: BarRow) : IRenderable =
    let tone = Terminal.styleOfTone bar.Tone
    let column = ProgressBarColumn()
    column.Width <- Nullable bar.Width
    column.CompletedStyle <- tone
    column.FinishedStyle <- tone
    column.IndeterminateStyle <- tone
    column.RemainingStyle <- remainingStyle

    let task = ProgressTask(0, "bar", 100.0)

    match bar.Fill with
    | BarFill.Filled percent -> task.Value <- Math.Clamp(percent, 0.0, 100.0)
    | BarFill.Indeterminate -> task.IsIndeterminate <- true

    { new IRenderable with
        member _.Measure(_, maxWidth) = Measurement(bar.Width, min bar.Width maxWidth)

        member _.Render(options, maxWidth) =
            (column.Render(options, task, TimeSpan.Zero)).Render(options, maxWidth) }

/// A bar line: the label, the native bar and the trailing field side by side, each in a column of its own width. A
/// `Grid` places them on one line where `Columns` would stack the greedy bar onto its own row.
let private barLine (bar: BarRow) : IRenderable =
    let column (width: int) = GridColumn(Width = width, NoWrap = true, Padding = Padding 0)
    let grid = Grid()
    grid.AddColumn(column bar.Label.Length).AddColumn(column bar.Width).AddColumn(column bar.Trailing.Length) |> ignore
    grid.AddRow(Text bar.Label, nativeBar bar, Text bar.Trailing) |> ignore
    grid :> IRenderable

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

let rec private renderLine (line: Line) : IRenderable =
    match line with
    | Line.Bar bar -> barLine bar
    | Line.Row cells -> Paragraph(cells |> List.map _.Text |> String.concat "", Style.Plain) :> IRenderable
    | Line.Text(tone, text) -> Text(text, Terminal.styleOfTone tone) :> IRenderable
    | Line.Spans spans -> spansLine spans
    | Line.Table table -> tableOf table
    | Line.Box(title, lines) ->
        let panel = Panel(Rows(lines |> List.map renderLine))
        panel.Border <- BoxBorder.Rounded
        if title.Length > 0 then panel.Header <- PanelHeader title
        panel :> IRenderable

let private render (display: Display) : IRenderable =
    Rows(display |> List.map renderLine) :> IRenderable

/// Prints a display once, in colour, below what is already on the console. A table or a box ends its own line; any
/// other widget is given a line break.
let print (console: IAnsiConsole) (display: Display) : unit =
    let shown = if console.Profile.Capabilities.Unicode then display else Display.mapTexts Display.toAscii display

    for line in shown do
        console.Write(renderLine line)

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
            let draw (display: Display) : unit =
                context.UpdateTarget(render display)
                context.Refresh()

            body draw)
    )
