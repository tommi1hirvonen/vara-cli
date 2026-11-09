/// Tests of the renderers and of the bracket that shows a run's progress. Each module is named after a requirement of
/// openspec/specs/progress-reporting/spec.md and each test after one of its scenarios.
module Vara.Cli.Tests.RunProgressTests

open System
open System.Threading.Tasks
open Spectre.Console
open Spectre.Console.Testing
open Xunit
open Vara.Cli
open Vara.Domain
open Vara.Engine

let private hasEscape (text: string) : bool = text.Contains(char 27)

let private clock () : DateTimeOffset = DateTimeOffset.UtcNow

/// What a screen was asked to show, in order.
type private Rig =
    { Draws: ResizeArray<Display>
      Lines: ResizeArray<string>
      Indicators: ResizeArray<string> }

let private rigAndScreen (mode: OutputMode) (indicator: bool) (heartbeat: TimeSpan) : Rig * RunProgress.Screen =
    let rig =
        { Draws = ResizeArray()
          Lines = ResizeArray()
          Indicators = ResizeArray() }

    rig,
    { Mode = mode
      Draw = rig.Draws.Add
      Width = fun () -> 100
      WriteLine = rig.Lines.Add
      WriteIndicator = if indicator then ValueSome rig.Indicators.Add else ValueNone
      Clock = clock
      Wording = Display.backupWording
      Initial = Tracker.initial
      Heartbeat = heartbeat }

let private succeed (_: 'a) : Outcome = Outcome.Success

let private wait (task: Task<'a>) : 'a = task.GetAwaiter().GetResult()

let private barOf (display: Display) : BarRow =
    display |> List.pick (function Line.Bar bar -> Some bar | _ -> None)

/// The backup's way of running with progress: the events a run publishes reach it as the engine's observers.
let private withObservers (screen: RunProgress.Screen) (run: Observers -> Task<'a>) (outcomeOf: 'a -> Outcome) : Task<'a> =
    RunProgress.withProgress screen (fun publish -> run (BackupCommand.observersFor screen.Mode publish)) outcomeOf

let private totals (bytes: int64) (files: int) : PlanTotals = { Bytes = bytes; Files = files }

module ``Progress delivery completes before display teardown`` =
    [<Fact>]
    let ``Final progress report delivered asynchronously`` () =
        let rig, screen = rigAndScreen OutputMode.Live false (TimeSpan.FromSeconds 60.0)
        let captured = ref Observers.none

        let result =
            withObservers
                screen
                (fun observers ->
                    captured.Value <- observers
                    observers.TransferPhaseStarting(totals 10L 1)
                    // The final report comes from another thread, and the run returns without waiting for it.
                    Task.Run(fun () ->
                        observers.BytesCopied.Value 10L
                        observers.FileTransferred())
                    |> ignore

                    Task.FromResult 7)
                succeed
            |> wait

        Assert.Equal(7, result)
        // Teardown is over: a report that arrives now is not drawn, and does not throw.
        Task.Delay(50).Wait()
        let drawn = rig.Draws.Count
        Task.Run(fun () -> captured.Value.BytesCopied.Value 5L).Wait()
        Task.Run(fun () -> captured.Value.FileStarted "late").Wait()
        Assert.Equal(drawn, rig.Draws.Count)

    [<Fact>]
    let ``every report made before the run returns is drawn before the final screen`` () =
        let rig, screen = rigAndScreen OutputMode.Live false (TimeSpan.FromSeconds 60.0)

        withObservers
            screen
            (fun observers ->
                observers.TransferPhaseStarting(totals 100L 1)
                observers.FileStarted "a"
                Task.FromResult 0)
            succeed
        |> wait
        |> ignore

        let lastBar = barOf (Seq.last rig.Draws)
        Assert.Equal(Tone.Success, lastBar.Tone)
        Assert.Equal(BarFill.Filled 100.0, lastBar.Fill)

module ``Progress heartbeat during stalled transfers`` =
    [<Fact>]
    let ``Heartbeat stops once the run completes`` () =
        let rig, screen = rigAndScreen OutputMode.Live false (TimeSpan.FromMilliseconds 20.0)

        withObservers
            screen
            (fun observers ->
                observers.TransferPhaseStarting(totals 100L 1)
                // A stall: no bytes for a while, so only the heartbeat draws.
                task {
                    do! Task.Delay 300
                    return 0
                })
            succeed
        |> wait
        |> ignore

        Assert.True(rig.Draws.Count >= 5, $"the heartbeat drew while the run stalled ({rig.Draws.Count} draws)")
        let afterRun = rig.Draws.Count
        Task.Delay(150).Wait()
        Assert.Equal(afterRun, rig.Draws.Count)

    [<Fact>]
    let ``Transfer stalls with no new bytes reported`` () =
        let rig, screen = rigAndScreen OutputMode.Live false (TimeSpan.FromMilliseconds 20.0)

        withObservers
            screen
            (fun observers ->
                observers.TransferPhaseStarting(totals 100L 1)
                observers.BytesCopied.Value 50L

                task {
                    do! Task.Delay 400
                    return 0
                })
            succeed
        |> wait
        |> ignore

        let etaOf (display: Display) : string =
            display
            |> List.pick (function
                | Line.Row [ _; eta ] when eta.Text.StartsWith "ETA" -> Some(eta.Text.TrimEnd())
                | _ -> None)

        let etas =
            rig.Draws
            |> Seq.filter (fun display -> display |> List.exists (function Line.Row _ -> true | _ -> false))
            |> Seq.map etaOf
            |> Seq.distinct
            |> List.ofSeq

        // The bytes shown never changed after 50, yet the estimate kept moving with the clock.
        Assert.True(etas.Length >= 2, $"the ETA changed during the stall: {etas}")

module ``Progress bar reflects run outcome severity`` =
    [<Fact>]
    let ``Run terminates with a hard error`` () =
        let rig, screen = rigAndScreen OutputMode.Live false (TimeSpan.FromSeconds 60.0)

        let failure =
            Assert.Throws<InvalidOperationException>(fun () ->
                withObservers
                    screen
                    (fun observers ->
                        observers.TransferPhaseStarting(totals 100L 1)
                        raise (InvalidOperationException "boom"))
                    succeed
                |> wait
                |> ignore)

        Assert.Equal("boom", failure.Message)
        Assert.Equal(Tone.Error, (barOf (Seq.last rig.Draws)).Tone)

    [<Fact>]
    let ``Run completes with one or more failed files`` () =
        let rig, screen = rigAndScreen OutputMode.Live false (TimeSpan.FromSeconds 60.0)

        withObservers
            screen
            (fun observers ->
                observers.TransferPhaseStarting(totals 100L 2)
                observers.BytesCopied.Value 50L
                Task.FromResult 0)
            (fun _ -> Outcome.PartialFailure)
        |> wait
        |> ignore

        let bar = barOf (Seq.last rig.Draws)
        Assert.Equal(Tone.PartialFailure, bar.Tone)
        Assert.Equal(BarFill.Filled 100.0, bar.Fill)

module ``Windows Terminal progress indicator`` =
    [<Fact>]
    let ``Command completes or stops`` () =
        // Completion, cancellation and failure all leave the indicator cleared.
        let clear = TerminalProgress.sequence Indicator.Hidden

        for body in
            [ (fun (_: Observers) -> Task.FromResult 0)
              (fun _ -> Task.FromCanceled<int>(Threading.CancellationToken(true)))
              (fun _ -> raise (InvalidOperationException "boom")) ] do
            let rig, screen = rigAndScreen OutputMode.Live true (TimeSpan.FromSeconds 60.0)

            try
                withObservers screen body succeed |> wait |> ignore
            with _ ->
                ()

            Assert.Equal(clear, Seq.last rig.Indicators)

    [<Fact>]
    let ``Indeterminate phase`` () =
        let rig, screen = rigAndScreen OutputMode.Live true (TimeSpan.FromSeconds 60.0)
        withObservers screen (fun _ -> Task.FromResult 0) succeed |> wait |> ignore
        Assert.Equal(TerminalProgress.sequence Indicator.Indeterminate, Seq.head rig.Indicators)

    [<Fact>]
    let ``Determinate phase`` () =
        let rig, screen = rigAndScreen OutputMode.Live true (TimeSpan.FromSeconds 60.0)

        withObservers
            screen
            (fun observers ->
                observers.TransferPhaseStarting(totals 200L 1)
                Task.FromResult 0)
            succeed
        |> wait
        |> ignore

        Assert.Contains(TerminalProgress.sequence (Indicator.Percentage 0), rig.Indicators)

module ``Terminal progress compatibility`` =
    /// Runs a short backup-like body through the real display on a console and returns what it wrote.
    let private written (mode: OutputMode) (session: string | null) : string =
        let console = (new TestConsole()).EmitAnsiSequences().Interactive()
        console.Profile.Width <- 100

        let env: CliEnv =
            { Console = console
              Capabilities = { IsTerminal = true; SupportsColor = true; NoColorRequested = false; InputIsInteractive = true }
              ErrorConsole = new TestConsole()
              JsonOut = new IO.StringWriter()
              Prompts = BackupCommandTests.noPrompts
              CurrentDirectory = Environment.CurrentDirectory
              UtcOffsetAt = fun _ -> TimeSpan.Zero
              OpenStandardOutput = fun () -> IO.Stream.Null
              MakeBackupEnv = BackupRun.live
              Power = PowerRequest.win32
              TerminalSession = session
              Alternate = BackupCommandTests.noScreen
              Persist = fun path _ -> failwith $"unexpected write of {path}"
              Exit = fun code -> failwith $"unexpected exit {code}" }

        ProgressDisplay.show
            env
            mode
            clock
            (fun publish ->
                let observers = BackupCommand.observersFor mode publish
                observers.TransferPhaseStarting(totals 100L 1)

                match observers.BytesCopied with
                | ValueSome report -> report 100L
                | ValueNone -> ()

                Task.FromResult 0)
            succeed
        |> wait
        |> ignore

        console.Output

    let private indicatorMarker = string (char 27) + "]9;4;"

    [<Fact>]
    let ``Progress in a supported terminal`` () =
        Assert.True((written OutputMode.Live "session").Contains(indicatorMarker, StringComparison.Ordinal))

    [<Fact>]
    let ``Redirected or machine-readable output`` () =
        Assert.False((written OutputMode.Plain "session").Contains(indicatorMarker, StringComparison.Ordinal))
        Assert.False((written OutputMode.Json "session").Contains(indicatorMarker, StringComparison.Ordinal))

    [<Fact>]
    let ``Another terminal host`` () =
        let output = written OutputMode.Live null
        Assert.False(output.Contains(indicatorMarker, StringComparison.Ordinal))
        // The display itself is unchanged.
        Assert.Contains("Backing up...", output)

module ``Live and plain renderers`` =
    [<Fact>]
    let ``the plain renderer writes no control sequences`` () =
        let console = (new TestConsole()).EmitAnsiSequences()

        PlainRenderer.write
            console
            [ Line.Text(Tone.Error, "failed")
              Line.Bar
                  { Label = " Backing up..."
                    Width = 20
                    Fill = BarFill.Filled 50.0
                    Trailing = "  50.0%"
                    Tone = Tone.PartialFailure }
              Line.Row [ { Width = 8; Text = "1 KB    " }; { Width = 6; Text = "Files " } ] ]

        Assert.False(hasEscape console.Output)
        Assert.Contains("failed", console.Output)
        Assert.Contains("Backing up...  50.0%", console.Output)

    [<Fact>]
    let ``the live renderer draws the first and last update at once`` () =
        let console = (new TestConsole()).EmitAnsiSequences().Interactive()
        console.Profile.Width <- 80

        LiveRenderer.run console (fun draw ->
            draw [ Line.Text(Tone.Neutral, "first-update") ]
            draw [ Line.Text(Tone.Success, "last-update") ]
            Task.FromResult 0)
        |> wait
        |> ignore

        Assert.Contains("first-update", console.Output)
        Assert.Contains("last-update", console.Output)

module ``Plain progress output`` =
    [<Fact>]
    let ``Non-interactive transfer output`` () =
        let rig, screen = rigAndScreen OutputMode.Plain false (TimeSpan.FromSeconds 60.0)

        withObservers
            screen
            (fun observers ->
                observers.TransferPhaseStarting(totals 100L 2)
                observers.BytesCopied.Value 60L
                observers.FileTransferred()
                Task.FromResult 0)
            succeed
        |> wait
        |> ignore

        // The first update, and the final one at once; the lines in between wait for the interval.
        Assert.Contains("0 B / 100 B (0.0%) - Files 0 / 2", rig.Lines[0])
        Assert.Contains("60 B / 100 B (60.0%) - Files 1 / 2", Seq.last rig.Lines)
        Assert.Empty rig.Draws

    [<Fact>]
    let ``JSON output installs no observers and draws nothing`` () =
        let rig, screen = rigAndScreen OutputMode.Json true (TimeSpan.FromSeconds 60.0)
        let seen = ref Observers.none

        withObservers
            screen
            (fun observers ->
                seen.Value <- observers
                Task.FromResult 0)
            succeed
        |> wait
        |> ignore

        Assert.True seen.Value.BytesCopied.IsNone
        Assert.Empty rig.Draws
        Assert.Empty rig.Lines
        Assert.Empty rig.Indicators
