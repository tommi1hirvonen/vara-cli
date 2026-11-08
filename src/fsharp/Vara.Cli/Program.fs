module Vara.Cli.Program

open System
open System.CommandLine

[<EntryPoint>]
let main argv =
    let cancellation = GracefulCancellation(fun exitCode -> Environment.Exit exitCode)
    Console.CancelKeyPress.Add(fun args -> args.Cancel <- cancellation.RequestCancellation())

    // A null termination timeout keeps System.CommandLine from installing a competing Ctrl+C handler.
    let configuration = InvocationConfiguration(ProcessTerminationTimeout = Nullable())

    Composition
        .rootCommand()
        .Parse(argv)
        .InvokeAsync(configuration, cancellation.Token)
        .GetAwaiter()
        .GetResult()
