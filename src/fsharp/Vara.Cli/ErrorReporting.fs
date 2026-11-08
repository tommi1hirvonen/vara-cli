/// Hard errors in the shared style (cli-presentation: "Unclassified exceptions are rendered in the hard-error style").
module Vara.Cli.ErrorReporting

open Spectre.Console
open Vara.Domain

/// An invalid option combination detected before any work starts.
let usage (console: IAnsiConsole) (message: string) : Outcome =
    Terminal.writeLine console Terminal.errorStyle $"Error: {message}"
    Outcome.HardError

/// Formatted by hand: AnsiConsole.WriteException is not supported under Native AOT.
let unhandled (console: IAnsiConsole) (error: exn) : Outcome =
    Terminal.writeLine console Terminal.errorStyle $"Unhandled exception: {error.GetType().Name}: {error.Message}"

    match error.StackTrace with
    | null
    | "" -> ()
    | stackTrace -> Terminal.writeLine console Terminal.detailStyle stackTrace

    Outcome.HardError
