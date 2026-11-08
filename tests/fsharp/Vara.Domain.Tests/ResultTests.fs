module Vara.Domain.Tests.ResultTests

open Xunit
open Vara.Domain

[<Fact>]
let ``result stops at the first error and runs nothing after it`` () =
    let laterStepRan = ref false

    let outcome =
        result {
            let! first = Ok 1
            let! _ = Error "first failure"
            laterStepRan.Value <- true
            let! second = Error "second failure"
            return first + second
        }

    Assert.Equal<Result<int, string>>(Error "first failure", outcome)
    Assert.False laterStepRan.Value

[<Fact>]
let ``result returns the value when every step succeeds`` () =
    let outcome =
        result {
            let! a = Ok 1
            let! b = Ok 2
            return a + b
        }

    Assert.Equal<Result<int, string>>(Ok 3, outcome)

[<Fact>]
let ``traverse keeps the order of the items`` () =
    let outcome = Result.traverse (fun n -> Ok(n * 10)) [ 1; 2; 3 ]
    Assert.Equal<Result<int list, string>>(Ok [ 10; 20; 30 ], outcome)

[<Fact>]
let ``traverse reports the first error and does not look at later items`` () =
    let looked = System.Collections.Generic.List<int>()

    let check n =
        looked.Add n
        if n % 2 = 0 then Error $"{n} is even" else Ok n

    let outcome = Result.traverse check [ 1; 2; 3; 4 ]
    Assert.Equal<Result<int list, string>>(Error "2 is even", outcome)
    Assert.Equal<int list>([ 1; 2 ], List.ofSeq looked)
