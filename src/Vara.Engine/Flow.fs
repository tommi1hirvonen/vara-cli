/// Generic combinators the imperative shell is built from.
module Vara.Engine.Flow

open System
open System.Collections.Generic
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Folds every item the channel yields until its writer completes.
let foldChannel (folder: 'State -> 'T -> 'State) (state: 'State) (reader: ChannelReader<'T>) : Task<'State> =
    let folder = OptimizedClosures.FSharpFunc<'State, 'T, 'State>.Adapt folder

    task {
        let mutable state = state

        while! reader.WaitToReadAsync() do
            let mutable draining = true

            while draining do
                match reader.TryRead() with
                | true, item -> state <- folder.Invoke(state, item)
                | _ -> draining <- false

        return state
    }

/// Runs work over the items with bounded parallelism; everything the workers emit arrives on one channel.
/// shouldStop is checked before each item starts, so no item is abandoned halfway.
let parallelEmit
    (degree: int)
    (shouldStop: unit -> bool)
    (work: ('Event -> unit) -> 'T -> unit)
    (items: IEnumerable<'T>)
    : struct (ChannelReader<'Event> * Task) =
    let channel =
        Channel.CreateUnbounded<'Event>(UnboundedChannelOptions(SingleReader = true, SingleWriter = false))

    let work = work (fun event -> channel.Writer.TryWrite event |> ignore)

    let completion =
        task {
            try
                do!
                    Parallel.ForEachAsync(
                        items,
                        ParallelOptions(MaxDegreeOfParallelism = degree),
                        Func<'T, CancellationToken, ValueTask>(fun item _ ->
                            if not (shouldStop ()) then
                                work item

                            ValueTask.CompletedTask)
                    )
            finally
                channel.Writer.Complete()
        }

    struct (channel.Reader, completion :> Task)

/// Captures a failure as a value, so cleanup can run before it is rethrown with its original stack.
let attempt (work: Task<'T>) : Task<Result<'T, ExceptionDispatchInfo>> =
    task {
        try
            let! value = work
            return Ok value
        with error ->
            return Error(ExceptionDispatchInfo.Capture error)
    }

let rethrow (error: ExceptionDispatchInfo) : 'T =
    error.Throw()
    Unchecked.defaultof<'T>
