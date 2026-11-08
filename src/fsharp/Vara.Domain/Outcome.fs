namespace Vara.Domain

/// How a command ended; its severity decides the process exit code.
[<RequireQualifiedAccess>]
type Outcome =
    | Success
    | PartialFailure
    | HardError

module Outcome =
    let exitCode (outcome: Outcome) : int =
        match outcome with
        | Outcome.Success -> 0
        | Outcome.HardError -> 1
        | Outcome.PartialFailure -> 2

    /// The more severe of two outcomes, so a hard error always wins.
    let combine (left: Outcome) (right: Outcome) : Outcome =
        match left, right with
        | Outcome.HardError, _
        | _, Outcome.HardError -> Outcome.HardError
        | Outcome.PartialFailure, _
        | _, Outcome.PartialFailure -> Outcome.PartialFailure
        | Outcome.Success, Outcome.Success -> Outcome.Success
