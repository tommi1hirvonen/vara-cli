/// Properties of the profile draft and the editor: what the editor writes is what the loader reads, and what a screen
/// says depends on the draft it shows and on nothing that came before it.
module Vara.Domain.Tests.ProfileEditorPropertyTests

open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain

/// Generators of profiles, shared with the engine tests that write them to a file.
module Generators =
    // Characters that a path, a glob or a name can carry and a YAML writer has to quote.
    let private segment: Gen<string> =
        Gen.elements [ "plain"; "a b"; "\u00e9"; "[x]"; "a#b"; "k=v"; "a,b"; "it's"; "{b}" ]

    let private glob: Gen<string> =
        Gen.elements [ "*.tmp"; "**/*.md"; "[ab]*"; "#note"; "k: v"; "a b"; "'q'"; "\"dq\""; "- x"; "yes"; "123" ]

    let private decoration: Gen<string> = Gen.elements [ ""; " x"; ":"; "\u00e9"; "#"; "true" ]

    let private flag: Gen<bool> = Gen.elements [ true; false ]

    let private globs: Gen<string list> =
        gen {
            let! count = Gen.choose (0, 2)
            return! Gen.listOfLength count glob
        }

    let private sourceDraft (profile: int) (index: int) : Gen<SourceDraft> =
        gen {
            let! seg = segment
            let! recursive = flag
            let! excludes = globs
            let! includes = globs
            let! excludeGlobs = globs

            return
                { SourcePath = Some $@"C:\src{profile}_{index}\{seg}"
                  IsRecursive = recursive
                  ExcludeEntries = excludes
                  IncludeGlobEntries = includes
                  ExcludeGlobEntries = excludeGlobs }
        }

    /// The generators run in order, their values in a list.
    let private sequence (gens: Gen<'T> list) : Gen<'T list> =
        gens
        |> List.fold
            (fun collected next ->
                gen {
                    let! values = collected
                    let! value = next
                    return values @ [ value ]
                })
            (Gen.constant [])

    let private count (low: int) (high: int) : Gen<string> = Gen.choose (low, high) |> Gen.map string

    let private retention: Gen<RetentionDraft option> =
        gen {
            let! present = flag

            if not present then
                return None
            else
                let! daily = count 0 50
                let! weekly = count 0 50
                let! monthly = count 0 50
                let! yearly = count 0 50

                return
                    Some
                        { DailyText = daily
                          WeeklyText = weekly
                          MonthlyText = monthly
                          YearlyText = yearly }
        }

    let private concurrency: Gen<ConcurrencyDraft option> =
        gen {
            let! present = flag

            if not present then
                return None
            else
                let! scan = Gen.optionOf (count 1 16)
                let! transfer = Gen.optionOf (count 1 16)
                return Some { ScanText = scan; TransferText = transfer }
        }

    let private profileDraft (index: int) : Gen<ProfileDraft> =
        gen {
            let! mark = decoration
            let! seg = segment
            let! sourceCount = Gen.choose (1, 3)
            let! sources = [ 0 .. sourceCount - 1 ] |> List.map (sourceDraft index) |> sequence
            let! retention = retention
            let! concurrency = concurrency

            return
                { Original = None
                  EnteredName = Some $"p{index}{mark}"
                  EnteredTarget = Some $@"C:\tgt{index}\{seg}"
                  DraftSources = sources
                  RetentionEntry = retention
                  ConcurrencyEntry = concurrency }
        }

    /// Lists of valid profiles, from none to four, with every optional field present or absent.
    let profiles: Gen<Profile list> =
        gen {
            let! count = Gen.choose (0, 4)
            let! drafts = [ 0 .. count - 1 ] |> List.map profileDraft |> sequence

            return
                drafts
                |> List.map (ProfileDraft.validate [])
                |> List.choose (fun outcome ->
                    match outcome with
                    | Ok profile -> Some profile
                    | Error _ -> None)
        }

    let arbitrary (gen: Gen<'T>) : Arbitrary<'T> = Arb.fromGen gen

let private arb = Generators.arbitrary

[<Property>]
let ``validating the tree of saved profiles returns the same profiles`` () =
    Prop.forAll (arb Generators.profiles) (fun profiles ->
        ProfileValidation.validateProfiles (ProfileNodes.ofProfiles profiles) = Ok profiles)

[<Property>]
let ``a draft made from a saved profile validates to that profile, among the others`` () =
    Prop.forAll (arb Generators.profiles) (fun profiles ->
        profiles
        |> List.forall (fun profile ->
            ProfileDraft.validate profiles (ProfileDraft.ofProfile profile) = Ok profile
            && ProfileDraft.validate [] (ProfileDraft.ofProfile profile) = Ok profile))

[<Property>]
let ``a draft that validates and is saved loads back to that profile`` () =
    let draftAndSaved =
        gen {
            let! saved = Generators.profiles
            let! extra = Generators.profiles
            return saved, extra |> List.tryHead
        }

    Prop.forAll (arb draftAndSaved) (fun (saved, extra) ->
        match extra with
        | None -> true
        | Some profile ->
            // Another list's first profile, renamed so that it is new here, as a draft of a new profile.
            let draft = { ProfileDraft.ofProfile profile with Original = None; EnteredName = Some "added" }

            match ProfileDraft.validate saved draft with
            | Error _ -> true
            | Ok validated ->
                let written = ProfileDraft.place saved draft validated
                ProfileValidation.validateProfiles (ProfileNodes.ofProfiles written) = Ok written
                && List.last written = validated)

let private inputs: Input list =
    [ Input.Fields(FieldsInput.SetName(Some "a"))
      Input.Fields(FieldsInput.SetName(Some "docs"))
      Input.Fields(FieldsInput.SetName None)
      Input.Fields(FieldsInput.SetTarget(Some @"C:\t"))
      Input.Fields(FieldsInput.SetTarget(Some @"C:\s\x"))
      Input.Fields(FieldsInput.SetTarget None)
      Input.Fields FieldsInput.ManageSources
      Input.Fields FieldsInput.EditRetention
      Input.Fields FieldsInput.EditConcurrency
      Input.Sources SourcesInput.Add
      Input.Sources(SourcesInput.Open 0)
      Input.Sources SourcesInput.Back
      Input.Source(SourceInput.SetPath(Some @"C:\s"))
      Input.Source(SourceInput.SetPath(Some "relative"))
      Input.Source(SourceInput.SetPath None)
      Input.Source(SourceInput.SetRecursive false)
      Input.Source(SourceInput.OpenList StringList.Excludes)
      Input.Source SourceInput.Remove
      Input.Source SourceInput.Back
      Input.Entries(EntriesInput.Add "x")
      Input.Entries(EntriesInput.Remove 0)
      Input.Entries EntriesInput.Done
      Input.Retention(RetentionInput.Set(RetentionKey.Daily, Some "3"))
      Input.Retention(RetentionInput.Set(RetentionKey.Daily, Some "x"))
      Input.Retention RetentionInput.Clear
      Input.Retention RetentionInput.Back
      Input.Concurrency(ConcurrencyInput.Set(ConcurrencyKey.Scan, Some "0"))
      Input.Concurrency ConcurrencyInput.Clear
      Input.Concurrency ConcurrencyInput.Back ]

[<Property>]
let ``a draft's validity never depends on an earlier state`` () =
    let docs = ProfileEditorTests.profileOf "docs" @"C:\backup\docs" [ @"C:\data\docs" ]

    let start =
        EditorState.Editing({ Saved = [ docs ]; Draft = ProfileDraft.empty; Notice = None }, Page.Fields)

    Prop.forAll (arb (Gen.listOf (Gen.elements inputs))) (fun steps ->
        let final = steps |> List.fold (fun state input -> fst (ProfileEditor.update state input)) start

        match final with
        | EditorState.Editing(session, page) ->
            // The same screen is shown for a state built from the final draft alone, so no history is in it, and the
            // validity it shows is the one that the draft has now.
            let fresh = EditorState.Editing({ session with Notice = None }, page)
            let shown = ProfileEditorView.view final = ProfileEditorView.view fresh

            let text = String.concat "\n" (Display.plainLines (ProfileEditorView.view final))

            // The list screen shows only its entries; every other screen shows the draft's error, when it has one.
            let validityShown =
                match page, ProfileDraft.validate session.Saved session.Draft with
                | Page.Entries _, _
                | _, Ok _ -> true
                | _, Error error -> text.Contains(ProfileDraft.describe error)

            shown && validityShown
        | _ -> true)
