/// Tests of the pieces the history commands share on the presentation side: bordered widgets, prompts, and the
/// progress of a restore. Each module is named after a requirement of openspec/specs and each test after one of its
/// scenarios.
module Vara.Cli.Tests.RestorePresentationTests

open System
open System.Threading.Tasks
open Spectre.Console.Testing
open Xunit
open Vara.Cli
open Vara.Domain
open Vara.Engine

let private clock () : DateTimeOffset = DateTimeOffset.UtcNow

let private wait (task: Task<'a>) : 'a = task.GetAwaiter().GetResult()

let private write (path: string) (size: int64) : RestoreWrite =
    { Source = (ContentHash.tryCreate "abc").Value
      Path = MirrorPath.ofString path
      Destination = path
      Size = size
      ModifiedAt = DateTimeOffset.UnixEpoch }

let private planOf (writes: RestoreWrite list) (removals: string list) : RestorePlan =
    { Writes = writes
      Removals = removals
      Skipped = []
      TotalBytes = writes |> List.sumBy _.Size }

let private large = 64L * 1024L * 1024L

let private liveConsole () : TestConsole =
    let console = (new TestConsole()).EmitAnsiSequences().Interactive()
    console.Profile.Width <- 100
    console

let private envOf (console: TestConsole) (mode: OutputMode) : CliEnv =
    { Console = console
      Capabilities =
        { IsTerminal = (mode = OutputMode.Live)
          SupportsColor = true
          NoColorRequested = false
          InputIsInteractive = true }
      ErrorConsole = new TestConsole()
      JsonOut = new IO.StringWriter()
      Prompts = BackupCommandTests.noPrompts
      CurrentDirectory = Environment.CurrentDirectory
      UtcOffsetAt = fun _ -> TimeSpan.Zero
      OpenStandardOutput = fun () -> IO.Stream.Null
      MakeBackupEnv = BackupRun.live
      Power = PowerRequest.win32
      TerminalSession = null
      Alternate = BackupCommandTests.noScreen
      Persist = fun path _ -> failwith $"unexpected write of {path}"
      Exit = fun code -> failwith $"unexpected exit {code}" }

let private tableDisplay: Display =
    HistoryView.ofSnapshots
        [ { SnapshotId = 1L
            StartedAt = DateTimeOffset.UnixEpoch
            CompletedAt = ValueNone
            Status = SnapshotStatus.Complete
            Stats =
              { FilesAdded = 1
                FilesChanged = 0
                FilesMoved = 0
                FilesDeleted = 0
                FilesFailed = 0
                BytesTransferred = 10L } } ]

module ``Bordered widgets use a consistent rounded-corner style`` =
    let private square = [ '\u250c'; '\u2510'; '\u2514'; '\u2518' ]

    [<Fact>]
    let ``A command renders a table`` () =
        let console = liveConsole ()
        LiveRenderer.print console tableDisplay
        let output = console.Output
        Assert.Contains("\u256d", output)
        Assert.Contains("\u2570", output)

        for corner in square do
            Assert.DoesNotContain(string corner, output)

    [<Fact>]
    let ``the plain output draws the same rounded border`` () =
        let lines = Display.plainLines tableDisplay
        Assert.StartsWith("\u256d", lines.Head)
        Assert.StartsWith("\u2570", List.last lines)

    [<Fact>]
    let ``a box is drawn with rounded corners too`` () =
        let console = liveConsole ()
        LiveRenderer.print console [ Line.Box("Title", [ Line.Text(Tone.Plain, "inside") ]) ]
        Assert.Contains("\u256d", console.Output)
        Assert.Contains("inside", console.Output)
        let plain = Display.plainLines [ Line.Box("Title", [ Line.Text(Tone.Plain, "inside") ]) ]
        Assert.StartsWith("\u256d", plain.Head)
        Assert.Contains("inside", plain[1])

module ``Prompt functions`` =
    let private onConsole (script: TestConsole -> unit) : Prompts * TestConsole =
        let console = (new TestConsole()).Interactive()
        script console
        Prompts.onConsole console, console

    [<Fact>]
    let ``selection returns the choice the user made`` () =
        let prompts, _ =
            onConsole (fun console ->
                console.Input.PushKey ConsoleKey.DownArrow
                console.Input.PushKey ConsoleKey.Enter)

        let choices = [ [ { Tone = Tone.Plain; Text = "first" } ]; [ { Tone = Tone.Success; Text = "second [x]" } ]; [ { Tone = Tone.Plain; Text = "third" } ] ]
        Assert.Equal(1, prompts.Select "Pick one:" choices 0)

    [<Fact>]
    let ``selection starts on the default`` () =
        let prompts, _ = onConsole (fun console -> console.Input.PushKey ConsoleKey.Enter)
        let choices = [ [ { Tone = Tone.Plain; Text = "a" } ]; [ { Tone = Tone.Plain; Text = "b" } ] ]
        Assert.Equal(1, prompts.Select "Pick one:" choices 1)

    [<Fact>]
    let ``confirmation returns yes or no`` () =
        let yes, _ = onConsole (fun console -> console.Input.PushTextWithEnter "y")
        Assert.True(yes.Confirm "Overwrite [file]?")
        let no, _ = onConsole (fun console -> console.Input.PushTextWithEnter "n")
        Assert.False(no.Confirm "Overwrite?")

    [<Fact>]
    let ``confirmation defaults to no`` () =
        let prompts, _ = onConsole (fun console -> console.Input.PushKey ConsoleKey.Enter)
        Assert.False(prompts.Confirm "Overwrite?")

    [<Fact>]
    let ``a scripted function can replace the prompts in a command test`` () =
        let asked = ResizeArray<string>()

        let scripted: Prompts =
            { Select =
                fun title choices _ ->
                    asked.Add title
                    choices.Length - 1
              Confirm =
                fun question ->
                    asked.Add question
                    true
              Text = fun prompt -> failwith $"unexpected text prompt: {prompt}" }

        let env = { envOf (liveConsole ()) OutputMode.Plain with Prompts = scripted }
        Assert.Equal(2, env.Prompts.Select "Which?" [ []; []; [] ] 0)
        Assert.True(env.Prompts.Confirm "Sure?")
        Assert.Equal<string list>([ "Which?"; "Sure?" ], List.ofSeq asked)

module ``Restore progress indication for large files`` =
    [<Fact>]
    let ``Restoring a large file`` () =
        let plan = planOf [ write "C:\\out\\big.bin" large ] []
        let initial = ProgressDisplay.restoreStart clock plan
        // The display starts in the transfer phase with the known total: a bar at 0%, never an indeterminate one.
        let first = Display.viewWith (ProgressDisplay.restoreWording "C:\\out\\big.bin") 100 DateTimeOffset.UtcNow initial

        match first with
        | Line.Bar bar :: _ ->
            Assert.Equal(BarFill.Filled 0.0, bar.Fill)
            Assert.Equal(large, initial.TotalBytes)
        | _ -> failwith "no bar"

        // It then advances as the content is copied.
        let advanced = initial |> Tracker.update Tracker.redrawInterval (DateTimeOffset.UtcNow.AddSeconds 1.0) (ProgressEvent.BytesCopied(large / 2L))

        match Display.viewWith (ProgressDisplay.restoreWording "C:\\out\\big.bin") 100 (DateTimeOffset.UtcNow.AddSeconds 1.0) advanced with
        | Line.Bar bar :: _ -> Assert.Equal(BarFill.Filled 50.0, bar.Fill)
        | _ -> failwith "no bar"

    [<Fact>]
    let ``Restoring a small file`` () =
        Assert.False(ProgressDisplay.worthShowing 1024L)
        Assert.True(ProgressDisplay.worthShowing large)
        // A small restore runs without any display: nothing is drawn or written, and the result is the run's own.
        let console = liveConsole ()
        let env = envOf console OutputMode.Live

        let result =
            ProgressDisplay.showRestore env OutputMode.Live clock "C:\\out\\a.txt" (planOf [ write "C:\\out\\a.txt" 1024L ] []) (fun _ -> Task.FromResult 7) (fun _ -> Outcome.Success)
            |> wait

        Assert.Equal(7, result)
        Assert.Equal("", console.Output)

    [<Fact>]
    let ``Non-interactive or redirected output`` () =
        let console = new TestConsole()
        let env = envOf console OutputMode.Plain
        let plan = planOf [ write "C:\\out\\big.bin" large ] []

        let run (publish: ProgressEvent -> unit) : Task<int> =
            publish (ProgressEvent.BytesCopied(large / 2L))
            publish ProgressEvent.FileTransferred
            Task.FromResult 0

        ProgressDisplay.showRestore env OutputMode.Plain clock "C:\\out\\big.bin" plan run (fun _ -> Outcome.Success) |> wait |> ignore
        let output = console.Output
        Assert.Contains("MB / 64 MB", output)
        Assert.False(output.Contains(char 27))

module ``Aggregate progress across a multi-file directory restore`` =
    let private plan =
        planOf [ write "C:\\out\\a.bin" (40L * 1024L * 1024L); write "C:\\out\\b.bin" (40L * 1024L * 1024L) ] [ "C:\\out\\stale.txt"; "C:\\out\\old.txt" ]

    let private percentAfter (events: ProgressEvent list) : float =
        let start = DateTimeOffset.UtcNow
        let mutable tracker = ProgressDisplay.restoreStart (fun () -> start) plan
        let mutable step = 0

        for event in events do
            step <- step + 1
            tracker <- Tracker.update Tracker.redrawInterval (start.AddSeconds(float step)) event tracker

        Tracker.percent tracker

    [<Fact>]
    let ``Restoring a directory with many files`` () =
        let mb = 1024L * 1024L
        // One indicator over the combined total: half-way through the second file it is at 75%, not restarting.
        Assert.Equal(25.0, percentAfter [ ProgressEvent.BytesCopied(20L * mb) ])
        Assert.Equal(50.0, percentAfter [ ProgressEvent.BytesCopied(40L * mb); ProgressEvent.FileTransferred ])

        Assert.Equal(
            75.0,
            percentAfter [ ProgressEvent.BytesCopied(40L * mb); ProgressEvent.FileTransferred; ProgressEvent.BytesCopied(20L * mb) ]
        )

    [<Fact>]
    let ``Restoring a directory whose files are all small`` () =
        let small = planOf [ write "C:\\out\\a" 100L; write "C:\\out\\b" 200L ] []
        Assert.False(ProgressDisplay.worthShowing small.TotalBytes)

    [<Fact>]
    let ``File removals during a directory restore do not distort progress`` () =
        let tracker = ProgressDisplay.restoreStart clock plan
        Assert.Equal(80L * 1024L * 1024L, tracker.TotalBytes)
        // Two removals are planned; only the two writes are files to transfer.
        Assert.Equal(2, tracker.TotalFiles)

    [<Fact>]
    let ``Non-interactive or redirected output`` () =
        let console = new TestConsole()
        let env = envOf console OutputMode.Plain

        let run (publish: ProgressEvent -> unit) : Task<int> =
            publish (ProgressEvent.BytesCopied(40L * 1024L * 1024L))
            publish ProgressEvent.FileTransferred
            Task.FromResult 0

        ProgressDisplay.showRestore env OutputMode.Plain clock "C:\\out" plan run (fun _ -> Outcome.Success) |> wait |> ignore
        Assert.Contains("Files 1 / 2", console.Output)
        Assert.False(console.Output.Contains(char 27))

module ``Restore task labels are bounded to a fixed width`` =
    let private bar (path: string) : BarRow =
        match Display.viewWith (ProgressDisplay.restoreWording path) 100 DateTimeOffset.UtcNow (ProgressDisplay.restoreStart clock (planOf [ write path 100L ] [])) with
        | Line.Bar bar :: _ -> bar
        | _ -> failwith "no bar"

    let private normalWidth = (bar "C:\\a.txt").Width

    [<Fact>]
    let ``Restoring a file at a short path`` () =
        let shown = bar "C\\a\\b.txt"
        Assert.Equal(" C\\a\\b.txt".PadRight Display.labelWidth, shown.Label)
        Assert.Equal(normalWidth, shown.Width)

    [<Fact>]
    let ``Restoring a file at a deeply nested path`` () =
        let shown = bar "C:\\Users\\someone\\Documents\\projects\\deep\\folder\\report.docx"
        Assert.Equal(Display.labelWidth, shown.Label.Length)
        Assert.StartsWith(" ...\\", shown.Label)
        Assert.Contains("report.docx", shown.Label)
        Assert.DoesNotContain("someone", shown.Label)
        Assert.Equal(normalWidth, shown.Width)

    [<Fact>]
    let ``Restoring a file whose name alone exceeds the budget`` () =
        let name = "an-extraordinarily-long-file-name-that-cannot-fit.txt"
        let shown = bar ("C:\\dir\\" + name)
        Assert.Equal(Display.labelWidth, shown.Label.Length)
        Assert.Contains("...", shown.Label)
        Assert.EndsWith(name.Substring(name.Length - 10), shown.Label.TrimEnd())
        Assert.Equal(normalWidth, shown.Width)

    [<Fact>]
    let ``Recursive directory restore at a deeply nested path`` () =
        let shown = bar "C:\\Users\\someone\\Documents\\projects\\deep\\folder\\restored"
        Assert.Equal(Display.labelWidth, shown.Label.Length)
        Assert.StartsWith(" ...\\", shown.Label)
        Assert.Equal(normalWidth, shown.Width)
