/// Interprets a `Display` as plain text: no colour, no cursor movement, nothing that redirected output could corrupt.
/// Used for redirected output, `NO_COLOR` and terminals without colour (cli-presentation: "Non-interactive or
/// color-incapable output falls back to plain text").
module Vara.Cli.PlainRenderer

open Spectre.Console
open Vara.Domain

/// The lines of a display. Its borders are drawn rounded where the console can show them and in ASCII where it cannot.
let lines (unicode: bool) (display: Display) : string list =
    if unicode then
        Display.plainLinesWith Display.roundedGlyphs display
    else
        Display.plainLinesWith Display.asciiGlyphs (Display.mapTexts Display.toAscii display)

/// Writes one unstyled line, exactly as it is: plain output is never wrapped at the console's width, so a table or a
/// path stays whole when it is read by a program.
let writeLine (console: IAnsiConsole) (text: string) : unit =
    let writer = console.Profile.Out.Writer
    writer.WriteLine text
    writer.Flush()

/// Writes every line unstyled, whatever tone it carries.
let write (console: IAnsiConsole) (display: Display) : unit =
    for text in lines console.Profile.Capabilities.Unicode display do
        writeLine console text
