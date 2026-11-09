namespace Vara.Domain

/// Builds a Result flow that reads in order and stops at the first Error. FSharp.Core has no builder for it.
type ResultBuilder() =
    member _.Return(value: 'T) : Result<'T, 'E> = Ok value
    member _.ReturnFrom(result: Result<'T, 'E>) : Result<'T, 'E> = result
    member _.Bind(result: Result<'T, 'E>, binder: 'T -> Result<'U, 'E>) : Result<'U, 'E> = Result.bind binder result
    member _.Zero() : Result<unit, 'E> = Ok()

[<AutoOpen>]
module ResultBuilderInstance =
    let result = ResultBuilder()

module Result =
    /// Applies the function to each item in order and collects the values; the first Error stops it.
    let traverse (f: 'T -> Result<'U, 'E>) (items: 'T list) : Result<'U list, 'E> =
        items
        |> List.fold (fun collected item -> collected |> Result.bind (fun values -> f item |> Result.map (fun value -> value :: values))) (Ok [])
        |> Result.map List.rev

    let ofOption (error: 'E) (value: 'T option) : Result<'T, 'E> =
        match value with
        | Some value -> Ok value
        | None -> Error error
