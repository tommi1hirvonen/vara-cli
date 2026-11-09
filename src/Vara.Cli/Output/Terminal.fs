/// Consoles and the outcome styles every command shares (cli-presentation: "Outcome severity is visually distinct").
module Vara.Cli.Terminal

open Spectre.Console
open Vara.Domain

let successStyle = Style(Color.PaleGreen1, decoration = Decoration.Bold)
let partialFailureStyle = Style(Color.LightGoldenrod2, decoration = Decoration.Bold)
let errorStyle = Style(Color.IndianRed, decoration = Decoration.Bold)
let neutralStyle = Style(Color.LightSkyBlue1, decoration = Decoration.Bold)
let detailStyle = Style(Color.Grey)
/// The colours of the kinds of change a snapshot records; the same wherever a kind is shown.
let addedStyle = Style(Color.PaleGreen1)
let changedStyle = Style(Color.LightGoldenrod2)
let movedStyle = Style(Color.LightSkyBlue1)
let deletedStyle = Style(Color.IndianRed)
let linkedStyle = Style(Color.Plum2)

let styleOf (outcome: Outcome) : Style =
    match outcome with
    | Outcome.Success -> successStyle
    | Outcome.PartialFailure -> partialFailureStyle
    | Outcome.HardError -> errorStyle

/// How each tone of a `Display` is drawn.
let styleOfTone (tone: Tone) : Style =
    match tone with
    | Tone.Plain -> Style.Plain
    | Tone.Detail -> detailStyle
    | Tone.Neutral -> neutralStyle
    | Tone.Success -> successStyle
    | Tone.PartialFailure -> partialFailureStyle
    | Tone.Error -> errorStyle
    | Tone.Added -> addedStyle
    | Tone.Changed -> changedStyle
    | Tone.Moved -> movedStyle
    | Tone.Deleted -> deletedStyle
    | Tone.Linked -> linkedStyle

/// What the console can do, for `OutputMode.choose`. `NO_COLOR` counts when set to anything but the empty string.
let capabilitiesOf (console: IAnsiConsole) : Capabilities =
    { IsTerminal = console.Profile.Out.IsTerminal
      SupportsColor = console.Profile.Capabilities.ColorSystem <> ColorSystem.NoColors
      NoColorRequested = not (System.String.IsNullOrEmpty(System.Environment.GetEnvironmentVariable "NO_COLOR"))
      InputIsInteractive = not System.Console.IsInputRedirected }

/// Standard error, so hard errors stay visible when standard output is redirected.
let standardError: IAnsiConsole =
    AnsiConsole.Create(AnsiConsoleSettings(Out = AnsiConsoleOutput(System.Console.Error)))

/// Writes text literally, so paths and messages are never parsed as markup.
let writeLine (console: IAnsiConsole) (style: Style) (text: string) : unit = console.WriteLine(text, style)
