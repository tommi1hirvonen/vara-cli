module Vara.Cli.Tests.HostTests

open System
open System.CommandLine
open System.IO
open System.Threading
open System.Threading.Tasks
open Spectre.Console.Testing
open Vara.Cli
open Vara.Domain
open Xunit

let private invoke (root: RootCommand) (args: string array) (cancellationToken: CancellationToken) =
    let output = new StringWriter()
    let configuration = InvocationConfiguration(Output = output, ProcessTerminationTimeout = Nullable())
    let exitCode = root.Parse(args).InvokeAsync(configuration, cancellationToken).GetAwaiter().GetResult()
    struct (exitCode, output.ToString())

let private rootWith (name: string) (errorConsole: TestConsole) handler =
    let root = Composition.rootCommand ()
    root.Subcommands.Add(Command(name) |> CommandAction.set errorConsole handler)
    root

[<Fact>]
let ``the first Ctrl+C requests a cooperative stop and the second forces a hard-error exit`` () =
    let mutable forcedExit = None
    let cancellation = GracefulCancellation(fun code -> forcedExit <- Some code)

    Assert.True(cancellation.RequestCancellation())
    Assert.True(cancellation.Token.IsCancellationRequested)
    Assert.Equal(None, forcedExit)

    Assert.False(cancellation.RequestCancellation())
    Assert.Equal(Some 1, forcedExit)

[<Fact>]
let ``a command's outcome becomes the process exit code`` () =
    let console = new TestConsole()
    let root = rootWith "partial" console (fun _ _ -> Task.FromResult Outcome.PartialFailure)
    let struct (exitCode, _) = invoke root [| "partial" |] CancellationToken.None
    Assert.Equal(2, exitCode)

[<Fact>]
let ``an exception escaping a command is reported in the hard-error style`` () =
    let console = new TestConsole()
    let root = rootWith "crash" console (fun _ _ -> raise (InvalidOperationException "boom"))
    let struct (exitCode, _) = invoke root [| "crash" |] CancellationToken.None
    Assert.Equal(1, exitCode)
    Assert.Contains("Unhandled exception: InvalidOperationException: boom", console.Output)

[<Fact>]
let ``the host's cancellation token reaches the command`` () =
    let console = new TestConsole()
    use source = new CancellationTokenSource()
    source.Cancel()

    let root =
        rootWith "observe" console (fun _ token ->
            Task.FromResult(if token.IsCancellationRequested then Outcome.PartialFailure else Outcome.Success))

    let struct (exitCode, _) = invoke root [| "observe" |] source.Token
    Assert.Equal(2, exitCode)

[<Fact>]
let ``help describes the tool`` () =
    let struct (exitCode, output) = invoke (Composition.rootCommand ()) [| "--help" |] CancellationToken.None
    Assert.Equal(0, exitCode)
    Assert.Contains("Vara - versioned, deduplicated backup tool", output)
