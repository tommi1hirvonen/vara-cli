/// Tests of the sleep-prevention bracket, with functions in place of the Win32 calls. Each module is named after a
/// requirement of openspec/specs/backup-execution/spec.md and each test after one of its scenarios.
module Vara.Engine.Tests.PowerRequestTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Vara.Engine

/// Calls that record what was asked of them, in order, and fail where the test says.
type private Recording =
    { Calls: PowerCalls
      Log: ResizeArray<string>
      Warnings: ResizeArray<string> }

let private recording (failCreate: bool) (failSet: bool) (failClear: bool) (failClose: bool) : Recording =
    let log = ResizeArray<string>()

    let step (name: string) (fails: bool) : Result<unit, int> =
        log.Add name
        if fails then Error 5 else Ok()

    { Calls =
        { Create =
            fun reason ->
                log.Add $"create {reason}"
                if failCreate then Error 5 else Ok 42n
          SetSystemRequired = fun handle -> step $"set {handle}" failSet
          ClearSystemRequired = fun handle -> step $"clear {handle}" failClear
          Close = fun handle -> step $"close {handle}" failClose }
      Log = log
      Warnings = ResizeArray<string>() }

let private run (rig: Recording) (body: unit -> Task<int>) : Task<int> =
    PowerRequest.withSleepPrevention rig.Calls rig.Warnings.Add body

module ``Automatic system sleep is prevented during a real backup`` =
    [<Fact>]
    let ``Successful real backup`` () =
        let rig = recording false false false false
        let result = (run rig (fun () -> rig.Log.Add "body"; Task.FromResult 7)).GetAwaiter().GetResult()
        Assert.Equal(7, result)
        // Requested before the body, released after it.
        Assert.Equal<string list>([ "create Vara backup in progress"; "set 42"; "body"; "clear 42"; "close 42" ], List.ofSeq rig.Log)
        Assert.Empty rig.Warnings

    [<Fact>]
    let ``Backup run is cancelled or fails`` () =
        let failing = recording false false false false
        let failure = Assert.Throws<InvalidOperationException>(fun () -> (run failing (fun () -> raise (InvalidOperationException "boom"))).GetAwaiter().GetResult() |> ignore)
        Assert.Equal("boom", failure.Message)
        Assert.Equal<string list>([ "clear 42"; "close 42" ], failing.Log |> Seq.skip 2 |> List.ofSeq)

        let cancelling = recording false false false false
        use source = new CancellationTokenSource()
        source.Cancel()

        Assert.ThrowsAny<OperationCanceledException>(fun () ->
            (run cancelling (fun () -> Task.FromCanceled<int> source.Token)).GetAwaiter().GetResult() |> ignore)
        |> ignore

        Assert.Equal<string list>([ "clear 42"; "close 42" ], cancelling.Log |> Seq.skip 2 |> List.ofSeq)

    [<Fact>]
    let ``Display timeout during backup`` () =
        // The request set is the system-required one, and nothing asks for the display: `PowerCalls` has no such call.
        let rig = recording false false false false
        (run rig (fun () -> Task.FromResult 0)).GetAwaiter().GetResult() |> ignore
        Assert.Equal(1, rig.Log |> Seq.filter (fun entry -> entry.StartsWith "set") |> Seq.length)

    [<Fact>]
    let ``the real Win32 request can be made and released`` () =
        let warnings = ResizeArray<string>()

        let result =
            (PowerRequest.withSleepPrevention PowerRequest.win32 warnings.Add (fun () -> Task.FromResult 3)).GetAwaiter().GetResult()

        Assert.Equal(3, result)
        Assert.Empty warnings

module ``Sleep-prevention request failure does not block backup`` =
    [<Fact>]
    let ``Windows refuses request activation`` () =
        for rig in [ recording true false false false; recording false true false false ] do
            let result = (run rig (fun () -> Task.FromResult 9)).GetAwaiter().GetResult()
            Assert.Equal(9, result)
            let warning = Assert.Single rig.Warnings
            Assert.StartsWith("Warning: Windows sleep prevention could not be activated:", warning)

        // A request that was created but not set is closed again.
        let notSet = recording false true false false
        (run notSet (fun () -> Task.FromResult 0)).GetAwaiter().GetResult() |> ignore
        Assert.Contains("close 42", notSet.Log)

    [<Fact>]
    let ``Windows refuses request cleanup`` () =
        let rig = recording false false true true
        let result = (run rig (fun () -> Task.FromResult 11)).GetAwaiter().GetResult()
        Assert.Equal(11, result)
        let warning = Assert.Single rig.Warnings
        Assert.StartsWith("Warning: Windows sleep prevention could not be released:", warning)
        Assert.Contains("PowerClearRequest", warning)
        Assert.Contains("CloseHandle", warning)

        // The body's failure stays the failure, when the cleanup fails as well.
        let failing = recording false false true false
        let failure = Assert.Throws<InvalidOperationException>(fun () -> (run failing (fun () -> raise (InvalidOperationException "body"))).GetAwaiter().GetResult() |> ignore)
        Assert.Equal("body", failure.Message)
        Assert.Single failing.Warnings |> ignore
