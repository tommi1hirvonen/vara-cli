/// Joins the pieces that show progress for the console: the live region, the appended lines and the Windows Terminal
/// indicator, each a function of a `Screen`.
module Vara.Cli.ProgressDisplay

open System
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

let private screenFor (env: CliEnv) (mode: OutputMode) (clock: unit -> DateTimeOffset) (draw: Display -> unit) : RunProgress.Screen =
    { Mode = mode
      Draw = draw
      Width = fun () -> LiveRenderer.width env.Console
      WriteLine = PlainRenderer.writeLine env.Console
      WriteIndicator =
        if TerminalProgress.enabled mode env.TerminalSession then
            ValueSome(LiveRenderer.writeSequence env.Console)
        else
            ValueNone
      Clock = clock
      Heartbeat = Tracker.redrawInterval }

/// Runs `run` with progress shown on the console in the given mode. The live region exists only for the live mode.
let show
    (env: CliEnv)
    (mode: OutputMode)
    (clock: unit -> DateTimeOffset)
    (run: Observers -> Task<'a>)
    (outcomeOf: 'a -> Outcome)
    : Task<'a> =
    match mode with
    | OutputMode.Live ->
        LiveRenderer.run env.Console (fun draw -> RunProgress.withProgress (screenFor env mode clock draw) run outcomeOf)
    | OutputMode.Plain
    | OutputMode.Json -> RunProgress.withProgress (screenFor env mode clock ignore) run outcomeOf
