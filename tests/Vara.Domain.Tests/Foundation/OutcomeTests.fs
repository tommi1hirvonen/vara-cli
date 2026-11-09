module Vara.Domain.Tests.OutcomeTests

// Xunit first: after `open FsCheck`, a bare `open Xunit` would resolve to FsCheck.Xunit.
open Xunit
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain

let private outcomes =
    Gen.elements [ Outcome.Success; Outcome.PartialFailure; Outcome.HardError ]
    |> Arb.fromGen

[<Fact>]
let ``each outcome has its documented exit code`` () =
    Assert.Equal(0, Outcome.exitCode Outcome.Success)
    Assert.Equal(1, Outcome.exitCode Outcome.HardError)
    Assert.Equal(2, Outcome.exitCode Outcome.PartialFailure)

[<Property>]
let ``combining outcomes is associative`` () =
    Prop.forAll (Arb.zip (outcomes, Arb.zip (outcomes, outcomes))) (fun (a, (b, c)) ->
        Outcome.combine a (Outcome.combine b c) = Outcome.combine (Outcome.combine a b) c)

[<Property>]
let ``success is the identity and a hard error always wins`` () =
    Prop.forAll outcomes (fun outcome ->
        Outcome.combine Outcome.Success outcome = outcome
        && Outcome.combine outcome Outcome.HardError = Outcome.HardError)
