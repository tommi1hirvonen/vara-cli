/// The command tree; each migration change adds its commands here.
module Vara.Cli.Composition

open System.CommandLine
open Spectre.Console
open Vara.Engine

let rootCommand () : RootCommand =
    RootCommand("Vara - versioned, deduplicated backup tool")

/// Adds the commands that run the engine. The consoles and the effects are passed in, so tests can replace them.
let withCommands
    (console: IAnsiConsole)
    (errorConsole: IAnsiConsole)
    (makeEnv: (unit -> bool) -> BackupEnv)
    (root: RootCommand)
    : RootCommand =
    root.Subcommands.Add(BackupCommand.create console errorConsole makeEnv)
    root
