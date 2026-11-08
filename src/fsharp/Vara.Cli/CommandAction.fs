/// How every command is wired: its handler returns an Outcome, which becomes the exit code, and anything
/// that escapes the handler is reported as a hard error.
module Vara.Cli.CommandAction

open System
open System.CommandLine
open System.Threading
open System.Threading.Tasks
open Spectre.Console
open Vara.Domain

let set
    (errorConsole: IAnsiConsole)
    (handler: ParseResult -> CancellationToken -> Task<Outcome>)
    (command: Command)
    : Command =
    command.SetAction(
        Func<ParseResult, CancellationToken, Task<int>>(fun parseResult cancellationToken ->
            task {
                try
                    let! outcome = handler parseResult cancellationToken
                    return Outcome.exitCode outcome
                with error ->
                    return Outcome.exitCode (ErrorReporting.unhandled errorConsole error)
            })
    )

    command
