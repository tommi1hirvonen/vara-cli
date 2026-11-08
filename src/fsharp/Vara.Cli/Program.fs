module Vara.Cli.Program

open System
open System.CommandLine
open Spectre.Console
open Vara.Engine

[<EntryPoint>]
let main argv =
    let cancellation = GracefulCancellation(fun exitCode -> Environment.Exit exitCode)
    Console.CancelKeyPress.Add(fun args -> args.Cancel <- cancellation.RequestCancellation())

    // A null termination timeout keeps System.CommandLine from installing a competing Ctrl+C handler.
    let configuration = InvocationConfiguration(ProcessTerminationTimeout = Nullable())

    let root =
        Composition.rootCommand ()
        |> Composition.withCommands AnsiConsole.Console Terminal.standardError BackupRun.live

    root.Parse(argv).InvokeAsync(configuration, cancellation.Token).GetAwaiter().GetResult()
