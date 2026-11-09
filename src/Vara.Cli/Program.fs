module Vara.Cli.Program

open System
open System.CommandLine

[<EntryPoint>]
let main argv =
    // Before any console is created: Spectre reads the output encoding then to decide whether it may draw bars and
    // borders. A Native AOT build has no code-page encodings, so the legacy console code page (850 here) would write
    // every such glyph as '?'.
    Console.OutputEncoding <- Text.UTF8Encoding false

    let cancellation = GracefulCancellation Environment.Exit
    Console.CancelKeyPress.Add(fun args -> args.Cancel <- cancellation.RequestCancellation())

    // A null termination timeout keeps System.CommandLine from installing a competing Ctrl+C handler.
    let configuration = InvocationConfiguration(ProcessTerminationTimeout = Nullable())

    let root =
        Composition.rootCommand () |> Composition.withCommands CliEnv.live

    root.Parse(argv).InvokeAsync(configuration, cancellation.Token).GetAwaiter().GetResult()
