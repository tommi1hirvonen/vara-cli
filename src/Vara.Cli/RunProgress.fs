/// Shows a run's progress. The bracket creates the tracker, hands the engine the observers that feed it, redraws on
/// each admitted update and on a heartbeat, and clears the display and the terminal indicator when the run ends,
/// however it ends. It performs its effects through the functions of a `Screen`.
module Vara.Cli.RunProgress

open System
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

/// What showing progress writes to, as functions, so a test replaces them.
type Screen =
    { Mode: OutputMode
      /// Redraws the live region.
      Draw: Display -> unit
      Width: unit -> int
      /// Appends a line of plain progress.
      WriteLine: string -> unit
      /// Writes a terminal-indicator sequence; absent unless the host terminal shows one.
      WriteIndicator: (string -> unit) voption
      Clock: unit -> DateTimeOffset
      /// The labels of the two phases.
      Wording: Display.Wording
      /// Where the display starts: a backup starts in its scan, a restore already knows its totals.
      Initial: Tracker
      /// The time between two heartbeat redraws.
      Heartbeat: TimeSpan }

/// Calls `tick` each time the timer fires, until the timer is disposed.
let private heartbeat (timer: System.Threading.PeriodicTimer) (tick: unit -> unit) : Task =
    task {
        // A loop over awaits: a recursive task would chain one task per tick for the length of the run.
        let mutable running = true

        while running do
            let! fired = timer.WaitForNextTickAsync()

            if fired then tick () else running <- false
    }

let private shown (screen: Screen) (run: (ProgressEvent -> unit) -> Task<'a>) (outcomeOf: 'a -> Outcome) : Task<'a> =
    task {
        // One lock serializes the observers (the engine's recorder), the heartbeat and the final draw. The cell is
        // local to this bracket and read only through these functions.
        let gate = obj ()
        let cell = ref screen.Initial
        let closed = ref false
        let lastSequence = ref ""
        let live = (screen.Mode = OutputMode.Live)

        let writeIndicator (tracker: Tracker) : unit =
            match screen.WriteIndicator with
            | ValueNone -> ()
            | ValueSome write ->
                let sequence = tracker |> TerminalProgress.indicatorBy screen.Wording.Basis |> TerminalProgress.sequence

                if sequence <> lastSequence.Value then
                    lastSequence.Value <- sequence
                    write sequence

        let drawLive (now: DateTimeOffset) (tracker: Tracker) : unit =
            screen.Draw(Display.viewWith screen.Wording (screen.Width()) now tracker)
            writeIndicator tracker

        // What the screen shows for a change from `before` to `after`.
        let present (now: DateTimeOffset) (before: Tracker) (after: Tracker) : unit =
            match screen.Mode with
            | OutputMode.Live -> if Tracker.visiblyChanged before after then drawLive now after
            | OutputMode.Plain ->
                if after.LastAdmitted <> before.LastAdmitted && after.Phase = Phase.Transferring then
                    screen.WriteLine(Display.plainProgressWith screen.Wording.Basis now after)
            | OutputMode.Json -> ()

        let publish (event: ProgressEvent) : unit =
            lock gate (fun () ->
                if not closed.Value then
                    let now = screen.Clock()
                    let before = cell.Value
                    let after = Tracker.update Tracker.redrawInterval now event before
                    cell.Value <- after
                    present now before after)

        let tick () : unit =
            lock gate (fun () ->
                if not closed.Value then
                    let now = screen.Clock()
                    let refreshed = Tracker.refresh Tracker.redrawInterval now cell.Value
                    cell.Value <- refreshed
                    drawLive now refreshed)

        if live then
            lock gate (fun () -> drawLive (screen.Clock()) cell.Value)

        let timer = new System.Threading.PeriodicTimer(screen.Heartbeat)
        let beating = if live then heartbeat timer tick else Task.CompletedTask

        // A run that throws before it returns a task is a failed run like any other.
        let! attempted = Flow.attempt (task { return! run publish })

        // The heartbeat stops before the final screen, so nothing redraws after it.
        timer.Dispose()
        do! beating

        try
            lock gate (fun () ->
                let outcome =
                    match attempted with
                    | Ok value -> outcomeOf value
                    | Error _ -> Outcome.HardError

                let now = screen.Clock()
                let before = cell.Value
                let after = Tracker.update Tracker.redrawInterval now (ProgressEvent.RunEnded outcome) before
                cell.Value <- after
                present now before after
                closed.Value <- true)
        finally
            lock gate (fun () ->
                closed.Value <- true

                match screen.WriteIndicator with
                | ValueSome write -> write (TerminalProgress.sequence Indicator.Hidden)
                | ValueNone -> ())

        match attempted with
        | Ok value -> return value
        | Error failure -> return Flow.rethrow failure
    }

/// Runs `run` with its progress shown as `screen` says. `run` is given the function that publishes a `ProgressEvent`;
/// what it does with it (the backup maps events to the engine's observers, a restore publishes them itself) is its
/// own. JSON output shows nothing and publishes to nowhere. A run that throws is shown as a hard error before the
/// exception continues.
let withProgress (screen: Screen) (run: (ProgressEvent -> unit) -> Task<'a>) (outcomeOf: 'a -> Outcome) : Task<'a> =
    match screen.Mode with
    | OutputMode.Json -> run ignore
    | OutputMode.Live
    | OutputMode.Plain -> shown screen run outcomeOf
