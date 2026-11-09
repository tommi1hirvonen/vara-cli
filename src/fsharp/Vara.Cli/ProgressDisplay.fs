/// Joins the pieces that show progress for the console: the live region, the appended lines and the Windows Terminal
/// indicator, each a function of a `Screen`.
module Vara.Cli.ProgressDisplay

open System
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

let private screenFor
    (env: CliEnv)
    (mode: OutputMode)
    (wording: Display.Wording)
    (initial: Tracker)
    (clock: unit -> DateTimeOffset)
    (draw: Display -> unit)
    : RunProgress.Screen =
    { Mode = mode
      Wording = wording
      Initial = initial
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

/// Below this many bytes a restore is over before a live display could be read: the display would flash, so the
/// summary alone is shown. The C# build has no such threshold (it draws a first and a last frame for every restore);
/// this is chosen so that a restore that finishes inside one redraw interval (`Tracker.redrawInterval`, 100 ms) at the
/// speed of an ordinary disk, about 80 MiB/s, never shows the display.
let private smallRestoreBytes: int64 = 8L * 1024L * 1024L

/// Whether a restore of that many bytes is long enough to show progress for (progress-reporting: "Restoring a small
/// file", "Restoring a directory whose files are all small").
let worthShowing (totalBytes: int64) : bool = totalBytes >= smallRestoreBytes

/// Runs `run` with progress shown on the console in the given mode, with the given labels, starting from the given
/// tracker. The live region exists only for the live mode.
let showWith
    (env: CliEnv)
    (mode: OutputMode)
    (wording: Display.Wording)
    (initial: Tracker)
    (clock: unit -> DateTimeOffset)
    (run: (ProgressEvent -> unit) -> Task<'a>)
    (outcomeOf: 'a -> Outcome)
    : Task<'a> =
    match mode with
    | OutputMode.Live ->
        LiveRenderer.run env.Console (fun draw ->
            RunProgress.withProgress (screenFor env mode wording initial clock draw) run outcomeOf)
    | OutputMode.Plain
    | OutputMode.Json -> RunProgress.withProgress (screenFor env mode wording initial clock ignore) run outcomeOf

/// Runs `run` with the backup's progress shown on the console in the given mode: it starts in the scan.
let show
    (env: CliEnv)
    (mode: OutputMode)
    (clock: unit -> DateTimeOffset)
    (run: (ProgressEvent -> unit) -> Task<'a>)
    (outcomeOf: 'a -> Outcome)
    : Task<'a> =
    showWith env mode Display.backupWording Tracker.initial clock run outcomeOf

/// The labels of a restore's progress screen: the destination, bounded to the label column's width (less its leading
/// space) by dropping parent directories from the left, so that the bar keeps its length however long the path is
/// (progress-reporting: "Restore task labels are bounded to a fixed width").
let restoreWording (destination: string) : Display.Wording =
    { Scanning = " Restoring..."
      Transferring = " " + PathLabel.truncate (Display.labelWidth - 1) destination }

/// A restore's tracker at the start: already in the transfer phase, with the plan's totals. Only the writes count, so
/// a removal, which transfers no bytes, cannot move the percentage, the rate or the ETA.
let restoreStart (clock: unit -> DateTimeOffset) (plan: RestorePlan) : Tracker =
    Tracker.apply (clock ()) (ProgressEvent.TransferStarting(plan.TotalBytes, plan.Writes.Length)) Tracker.initial

/// Runs a restore with progress shown. There is no scan phase: the display starts in the transfer phase with the
/// plan's totals, at 0% of a known size. A restore too small to read a display for shows none; one run with output
/// that cannot redraw appends plain lines. Removals are not part of the totals, so they cannot distort the rate.
let showRestore
    (env: CliEnv)
    (mode: OutputMode)
    (clock: unit -> DateTimeOffset)
    (label: string)
    (plan: RestorePlan)
    (run: (ProgressEvent -> unit) -> Task<'a>)
    (outcomeOf: 'a -> Outcome)
    : Task<'a> =
    if worthShowing plan.TotalBytes then
        showWith env mode (restoreWording label) (restoreStart clock plan) clock run outcomeOf
    else
        run ignore
