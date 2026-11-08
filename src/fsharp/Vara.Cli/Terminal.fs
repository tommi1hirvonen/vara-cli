/// Consoles and the outcome styles every command shares (cli-presentation: "Outcome severity is visually distinct").
module Vara.Cli.Terminal

open Spectre.Console
open Vara.Domain

let successStyle = Style(Color.PaleGreen1, decoration = Decoration.Bold)
let partialFailureStyle = Style(Color.LightGoldenrod2, decoration = Decoration.Bold)
let errorStyle = Style(Color.IndianRed, decoration = Decoration.Bold)
let neutralStyle = Style(Color.LightSkyBlue1, decoration = Decoration.Bold)
let detailStyle = Style(Color.Grey)

let styleOf (outcome: Outcome) : Style =
    match outcome with
    | Outcome.Success -> successStyle
    | Outcome.PartialFailure -> partialFailureStyle
    | Outcome.HardError -> errorStyle

/// Standard error, so hard errors stay visible when standard output is redirected.
let standardError: IAnsiConsole =
    AnsiConsole.Create(AnsiConsoleSettings(Out = AnsiConsoleOutput(System.Console.Error)))

/// Writes text literally, so paths and messages are never parsed as markup.
let writeLine (console: IAnsiConsole) (style: Style) (text: string) : unit = console.WriteLine(text, style)
