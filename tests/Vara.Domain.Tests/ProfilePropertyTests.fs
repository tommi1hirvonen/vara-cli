module Vara.Domain.Tests.ProfilePropertyTests

open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain

// Few distinct segments, so that generated paths often overlap.
let private segments = [ "a"; "b"; "A"; "B"; "."; ".."; "" ]
let private separators = [ @"\"; "/"; @"\\" ]

/// Text for a fully-qualified path, written with mixed separators, dots and doubled separators.
let private pathText: Gen<string> =
    gen {
        let! count = Gen.choose (0, 3)
        let! parts = Gen.listOfLength count (Gen.elements segments)
        let! separator = Gen.elements separators
        return "C:" + separator + String.concat separator parts
    }

let private paths: Gen<AbsolutePath> =
    pathText |> Gen.map (fun text -> Option.get (AbsolutePath.tryCreate text))

let private arb gen = Arb.fromGen gen

let private recase (path: AbsolutePath) : AbsolutePath =
    Option.get (AbsolutePath.tryCreate ((AbsolutePath.value path).ToUpperInvariant()))

let private toggleTrailingSeparator (path: AbsolutePath) : AbsolutePath =
    let text = AbsolutePath.value path

    let toggled =
        if text.EndsWith @"\" && text.Length > 3 then text.TrimEnd('\\') else text + @"\"

    Option.get (AbsolutePath.tryCreate toggled)

[<Property>]
let ``canonicalizing a path twice gives the same result as once`` () =
    Prop.forAll (arb pathText) (fun text ->
        let once = AbsolutePath.tryCreate text
        let twice = once |> Option.bind (AbsolutePath.value >> AbsolutePath.tryCreate)
        once = twice)

[<Property>]
let ``overlap is symmetric`` () =
    Prop.forAll (arb (Gen.zip paths paths)) (fun (left, right) ->
        AbsolutePath.overlaps left right = AbsolutePath.overlaps right left)

[<Property>]
let ``overlap ignores letter case`` () =
    Prop.forAll (arb (Gen.zip paths paths)) (fun (left, right) ->
        AbsolutePath.overlaps left right = AbsolutePath.overlaps (recase left) right)

[<Property>]
let ``overlap ignores a trailing separator`` () =
    Prop.forAll (arb (Gen.zip paths paths)) (fun (left, right) ->
        AbsolutePath.overlaps left right = AbsolutePath.overlaps left (toggleTrailingSeparator right))

[<Property>]
let ``a validated profile never has overlapping sources or a target that overlaps a source`` () =
    let source path =
        ConfigNode.Mapping [ ("path", ConfigNode.Scalar path) ]

    let entry =
        gen {
            let! target = pathText
            let! count = Gen.choose (1, 4)
            let! sources = Gen.listOfLength count pathText

            return
                ConfigNode.Mapping
                    [ "name", ConfigNode.Scalar "p"
                      "target", ConfigNode.Scalar target
                      "sources", ConfigNode.Sequence(sources |> List.map source) ]
        }

    Prop.forAll (arb entry) (fun entry ->
        match ProfileValidation.validateProfiles [ entry ] with
        | Error _ -> true
        | Ok profiles ->
            profiles
            |> List.forall (fun profile ->
                let sources = Profile.sources profile |> List.map _.Path

                let targetClear =
                    sources |> List.forall (fun path -> not (AbsolutePath.overlaps (Profile.target profile) path))

                let sourcesClear =
                    sources
                    |> List.indexed
                    |> List.forall (fun (index, first) ->
                        sources |> List.skip (index + 1) |> List.forall (fun second -> not (AbsolutePath.overlaps first second)))

                not sources.IsEmpty && targetClear && sourcesClear))
