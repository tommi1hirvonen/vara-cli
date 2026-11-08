/// Interprets a `Display` as plain text: no colour, no cursor movement, nothing that redirected output could corrupt.
/// Used for redirected output, `NO_COLOR` and terminals without colour (cli-presentation: "Non-interactive or
/// color-incapable output falls back to plain text").
module Vara.Cli.PlainRenderer

open Spectre.Console
open Vara.Domain

/// The text of one line. A bar has no plain form but its label and percentage.
let private textOf (line: Line) : string =
    match line with
    | Line.Text(_, text) -> text
    | Line.Row cells -> (cells |> List.map (fun cell -> cell.Text) |> String.concat "").TrimEnd()
    | Line.Bar bar -> (bar.Label + bar.Trailing).TrimEnd()

let lines (display: Display) : string list = display |> List.map textOf

/// Writes one unstyled line.
let writeLine (console: IAnsiConsole) (text: string) : unit = console.WriteLine(text, Style.Plain)

/// Writes every line unstyled, whatever tone it carries.
let write (console: IAnsiConsole) (display: Display) : unit =
    for text in lines display do
        writeLine console text
