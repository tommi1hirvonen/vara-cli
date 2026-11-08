namespace Vara.Cli

open System.Threading
open Vara.Domain

/// The first Ctrl+C requests a cooperative stop; a second one forces the exit the first suppressed
/// (backup-execution: "Graceful cancellation via Ctrl+C").
type GracefulCancellation(forceExit: int -> unit) =
    let source = new CancellationTokenSource()

    member _.Token = source.Token

    /// Returns whether the operating system's default termination should be suppressed.
    member _.RequestCancellation() : bool =
        if source.IsCancellationRequested then
            forceExit (Outcome.exitCode Outcome.HardError)
            false
        else
            source.Cancel()
            true
