/// The command tree; each migration change adds its commands here.
module Vara.Cli.Composition

open System.CommandLine

let rootCommand () : RootCommand =
    RootCommand("Vara - versioned, deduplicated backup tool")
