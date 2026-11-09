namespace Vara.Domain

open System

/// Where a run is: counting the scan's entries, copying content, in the one order a run goes through them.
[<RequireQualifiedAccess>]
type Phase =
    | Scanning of entries: int
    | Transferring

/// What the engine's observers report, as plain data. A struct, so a chunk of copied bytes allocates no event.
[<Struct>]
type ProgressEvent =
    | ScanCounted of entries: int
    | TransferStarting of totalBytes: int64 * totalFiles: int
    /// The bytes copied since the previous report. Reports may arrive in any order; only their sum matters.
    | BytesCopied of delta: int64
    | FileStarted of started: string
    | FileFinished of finished: string
    | FileTransferred
    /// Work that is done without reading any byte, such as a missing blob whose size counts as checked. It moves the
    /// percentage of a byte-weighted run but not the byte count that the throughput and the ETA come from.
    | WorkCredited of bytes: int64
    /// The run is over; its outcome colours the final bar.
    | RunEnded of outcome: Outcome

/// What a run's percentage and ETA are measured in: the bytes it moves, or the items it completes (blobs removed or
/// checked), which have no throughput.
[<RequireQualifiedAccess>]
type Basis =
    | Bytes
    | Items

/// Bytes and files, as the display shows them.
[<Struct>]
type Counted = { Bytes: int64; Files: int }

/// A run's progress as the display sees it. Immutable: each event yields a new value, so a heartbeat is only a later
/// clock value passed to `Display.view`. `Live` follows every event; `Shown` follows it only when a redraw is admitted,
/// which is what rate-limits the numbers without limiting the active-transfer list.
type Tracker =
    { Phase: Phase
      TotalBytes: int64
      TotalFiles: int
      Live: Counted
      Shown: Counted
      /// The bytes credited as done without being read, as reported and as last admitted.
      Credited: int64
      ShownCredited: int64
      /// The files being transferred, newest first.
      Active: string list
      /// When the transfer phase began: the basis of throughput, so a metadata pass before it does not count.
      TransferStartedAt: DateTimeOffset voption
      LastAdmitted: DateTimeOffset voption
      Ended: Outcome voption }

/// The rate limit on numeric redraws, as a pure test (progress-reporting: "Progress display updates are rate-limited").
module Throttle =
    /// Whether a redraw is due: nothing has been drawn yet, or the whole interval has passed since the last one.
    let admit (interval: TimeSpan) (lastAdmitted: DateTimeOffset voption) (now: DateTimeOffset) : bool =
        match lastAdmitted with
        | ValueNone -> true
        | ValueSome last -> now - last >= interval

module Tracker =
    /// The interval between two redraws of the numbers; the C# build uses the same.
    let redrawInterval = TimeSpan.FromMilliseconds 100.0

    let initial: Tracker =
        { Phase = Phase.Scanning 0
          TotalBytes = 0L
          TotalFiles = 0
          Live = { Bytes = 0L; Files = 0 }
          Shown = { Bytes = 0L; Files = 0 }
          Credited = 0L
          ShownCredited = 0L
          Active = []
          TransferStartedAt = ValueNone
          LastAdmitted = ValueNone
          Ended = ValueNone }

    let private removeFirst (path: string) (paths: string list) : string list =
        let rec go (rest: string list) (before: string list) =
            match rest with
            | [] -> paths
            | head :: tail when String.Equals(head, path, StringComparison.Ordinal) -> List.rev before @ tail
            | head :: tail -> go tail (head :: before)

        go paths []

    /// Folds one event into the tracker. The byte count only grows, so a late or reordered report never lowers it.
    let apply (now: DateTimeOffset) (event: ProgressEvent) (tracker: Tracker) : Tracker =
        match event with
        | ScanCounted entries ->
            match tracker.Phase with
            | Phase.Scanning _ -> { tracker with Phase = Phase.Scanning entries }
            | Phase.Transferring -> tracker
        | TransferStarting(totalBytes, totalFiles) ->
            { tracker with
                Phase = Phase.Transferring
                TotalBytes = totalBytes
                TotalFiles = totalFiles
                TransferStartedAt = ValueSome now }
        | BytesCopied delta ->
            { tracker with Live.Bytes = tracker.Live.Bytes + max 0L delta }
        | FileStarted path -> { tracker with Active = path :: tracker.Active }
        | FileFinished path -> { tracker with Active = removeFirst path tracker.Active }
        | FileTransferred -> { tracker with Live.Files = tracker.Live.Files + 1 }
        | WorkCredited bytes -> { tracker with Credited = tracker.Credited + max 0L bytes }
        | RunEnded outcome -> { tracker with Ended = ValueSome outcome }

    /// Whether the numbers are due for a redraw: always for the first transfer update and the final one, otherwise
    /// once the interval has passed. Nothing is drawn as numbers before the transfer phase.
    let private due (interval: TimeSpan) (now: DateTimeOffset) (tracker: Tracker) : bool =
        match tracker.Ended, tracker.Phase with
        | ValueSome _, _ -> true
        | ValueNone, Phase.Scanning _ -> false
        | ValueNone, Phase.Transferring -> Throttle.admit interval tracker.LastAdmitted now

    /// Moves the shown numbers up to the live ones, never past the planned total and never below what was shown.
    let private admit (now: DateTimeOffset) (tracker: Tracker) : Tracker =
        let live = tracker.Live.Bytes
        let capped = if tracker.TotalBytes > 0L then min live tracker.TotalBytes else live

        { tracker with
            Shown =
                { Bytes = max tracker.Shown.Bytes capped
                  Files = max tracker.Shown.Files tracker.Live.Files }
            ShownCredited = max tracker.ShownCredited tracker.Credited
            LastAdmitted = ValueSome now }

    /// Admits a redraw of the numbers when one is due. The renderer calls it on its timer, so numbers that stopped
    /// changing are still shown once the interval has passed.
    let refresh (interval: TimeSpan) (now: DateTimeOffset) (tracker: Tracker) : Tracker =
        if due interval now tracker then admit now tracker else tracker

    /// `apply`, then `refresh`: what an observer does with one event.
    let update (interval: TimeSpan) (now: DateTimeOffset) (event: ProgressEvent) (tracker: Tracker) : Tracker =
        tracker |> apply now event |> refresh interval now

    /// Whether the screen would look different: the phase changed, the shown numbers or the active list moved, or the
    /// run ended. The scan's running count and the live byte count do not show, so they never ask for a redraw.
    let visiblyChanged (before: Tracker) (after: Tracker) : bool =
        let phaseChanged =
            match before.Phase, after.Phase with
            | Phase.Scanning _, Phase.Scanning _ -> false
            | Phase.Transferring, Phase.Transferring -> false
            | Phase.Scanning _, Phase.Transferring
            | Phase.Transferring, Phase.Scanning _ -> true

        phaseChanged || before.Shown <> after.Shown || before.ShownCredited <> after.ShownCredited || before.Active <> after.Active || before.Ended <> after.Ended

    /// Seconds since the transfer phase began, or nothing before it.
    let private elapsed (now: DateTimeOffset) (tracker: Tracker) : float =
        match tracker.TransferStartedAt with
        | ValueSome started -> max 0.0 (now - started).TotalSeconds
        | ValueNone -> 0.0

    /// Bytes per second since the transfer phase began. Credited work is not read, so it is not counted.
    let throughput (now: DateTimeOffset) (tracker: Tracker) : float =
        let seconds = elapsed now tracker
        if seconds > 0.0 then float tracker.Shown.Bytes / seconds else 0.0

    /// Items per second since the transfer phase began.
    let itemRate (now: DateTimeOffset) (tracker: Tracker) : float =
        let seconds = elapsed now tracker
        if seconds > 0.0 then float tracker.Shown.Files / seconds else 0.0

    /// The bytes done so far: those read and those credited.
    let private bytesDone (tracker: Tracker) : int64 = tracker.Shown.Bytes + tracker.ShownCredited

    /// The time left in the measure of the basis, never negative; nothing while there is no rate to extrapolate. The
    /// rate is the observed one: bytes read per second, or items done per second.
    let remainingBy (basis: Basis) (now: DateTimeOffset) (tracker: Tracker) : TimeSpan voption =
        let struct (rate, left) =
            match basis with
            | Basis.Bytes -> struct (throughput now tracker, max 0L (tracker.TotalBytes - bytesDone tracker))
            | Basis.Items -> struct (itemRate now tracker, int64 (max 0 (tracker.TotalFiles - tracker.Shown.Files)))

        if rate > 0.0 then ValueSome(TimeSpan.FromSeconds(float left / rate)) else ValueNone

    /// The time left at the current throughput.
    let remaining (now: DateTimeOffset) (tracker: Tracker) : TimeSpan voption = remainingBy Basis.Bytes now tracker

    /// How much of the planned work is done, from 0 to 100. A run with nothing planned is complete.
    let percentBy (basis: Basis) (tracker: Tracker) : float =
        let struct (completed, total) =
            match basis with
            | Basis.Bytes -> struct (bytesDone tracker, tracker.TotalBytes)
            | Basis.Items -> struct (int64 tracker.Shown.Files, int64 tracker.TotalFiles)

        if total > 0L then Math.Clamp(float completed / float total * 100.0, 0.0, 100.0) else 100.0

    /// How much of the planned bytes were copied, from 0 to 100. A run with nothing to copy is complete.
    let percent (tracker: Tracker) : float = percentBy Basis.Bytes tracker
