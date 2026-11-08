/// The command tree; each migration change adds its commands here.
module Vara.Cli.Composition

open System.CommandLine

let rootCommand () : RootCommand =
    RootCommand("Vara - versioned, deduplicated backup tool")

/// Adds the commands that run the engine. The effects are passed in, so tests can replace them.
let withCommands (env: CliEnv) (root: RootCommand) : RootCommand =
    root.Subcommands.Add(BackupCommand.create env)
    root
