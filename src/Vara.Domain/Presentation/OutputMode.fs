namespace Vara.Domain

open System

/// What the console can do, as the composition root reads it.
type Capabilities =
    { IsTerminal: bool
      SupportsColor: bool
      /// The `NO_COLOR` convention is set.
      NoColorRequested: bool
      /// Standard input is not redirected, so a prompt can be answered.
      InputIsInteractive: bool }

/// How a command writes its output (cli-presentation: "Non-interactive or color-incapable output falls back to plain
/// text", "Machine-readable JSON output for the backup command").
[<RequireQualifiedAccess>]
type OutputMode =
    /// One JSON object on standard output and nothing else.
    | Json
    /// In-place redrawn progress and coloured text.
    | Live
    /// Appended, uncoloured lines.
    | Plain

module OutputMode =
    /// JSON wins; otherwise the live display needs a terminal that shows colour and has not been asked not to.
    let choose (json: bool) (capabilities: Capabilities) : OutputMode =
        if json then OutputMode.Json
        elif capabilities.IsTerminal && capabilities.SupportsColor && not capabilities.NoColorRequested then OutputMode.Live
        else OutputMode.Plain

/// What the Windows Terminal tab and taskbar show.
[<RequireQualifiedAccess>]
type Indicator =
    | Hidden
    | Indeterminate
    /// From 0 to 100.
    | Percentage of percent: int

module TerminalProgress =
    /// Hidden once the run has ended, indeterminate while the scan runs, the byte percentage while content copies.
    let indicatorBy (basis: Basis) (tracker: Tracker) : Indicator =
        match tracker.Ended, tracker.Phase with
        | ValueSome _, _ -> Indicator.Hidden
        | ValueNone, Phase.Scanning _ -> Indicator.Indeterminate
        | ValueNone, Phase.Transferring ->
            Indicator.Percentage(int (Math.Round(Math.Clamp(Tracker.percentBy basis tracker, 0.0, 100.0))))

    /// The indicator of a run measured in bytes.
    let indicator (tracker: Tracker) : Indicator = indicatorBy Basis.Bytes tracker

    /// The OSC 9;4 sequence for the indicator, ended by BEL.
    let sequence (indicator: Indicator) : string =
        match indicator with
        | Indicator.Hidden -> "\u001b]9;4;0;0\u0007"
        | Indicator.Indeterminate -> "\u001b]9;4;3;0\u0007"
        | Indicator.Percentage percent ->
            "\u001b]9;4;1;" + Math.Clamp(percent, 0, 100).ToString Globalization.CultureInfo.InvariantCulture + "\u0007"

    /// Whether the sequences are written at all: only for the live display, in Windows Terminal, which sets
    /// `WT_SESSION` (progress-reporting: "Terminal progress compatibility").
    let enabled (mode: OutputMode) (terminalSession: string | null) : bool =
        match mode, terminalSession with
        | OutputMode.Live, null -> false
        | OutputMode.Live, session -> not (String.IsNullOrWhiteSpace session)
        | OutputMode.Json, _
        | OutputMode.Plain, _ -> false
