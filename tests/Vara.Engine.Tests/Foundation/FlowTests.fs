module Vara.Engine.Tests.FlowTests

open System
open System.Threading.Tasks
open Vara.Engine
open Xunit

[<Fact>]
let ``every event emitted by the workers is folded once the pool completes`` () =
    task {
        let struct (events, pool) =
            Flow.parallelEmit 4 (fun () -> false) (fun emit item -> emit item; emit (item * 10)) [ 1..100 ]

        let! total = Flow.foldChannel (+) 0 events
        do! pool
        Assert.Equal(List.sum [ 1..100 ] * 11, total)
    }

[<Fact>]
let ``no item starts once a stop is requested`` () =
    task {
        let struct (events, pool) = Flow.parallelEmit 4 (fun () -> true) id [ 1..100 ]
        let! count = Flow.foldChannel (fun count _ -> count + 1) 0 events
        do! pool
        Assert.Equal(0, count)
    }

[<Fact>]
let ``a failure is captured as a value and rethrown with its original type`` () =
    task {
        let! result = Flow.attempt (Task.FromException<int>(InvalidOperationException "boom"))

        match result with
        | Ok _ -> Assert.Fail "Expected the failure to be captured."
        | Error error ->
            let rethrown = Assert.Throws<InvalidOperationException>(fun () -> Flow.rethrow error |> ignore)
            Assert.Equal("boom", rethrown.Message)
    }
