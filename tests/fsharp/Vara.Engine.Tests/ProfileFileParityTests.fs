/// The configuration file in both directions: profiles that the F# writer saves load in the C# loader with the same fields,
/// and a file that the C# writer saves loads in the F# loader with the same profiles. The interactive editor itself has no
/// process parity test: a child process cannot be driven through prompts, and a graceful Ctrl+C cannot be delivered to
/// one either, so the editor rests on the in-process tests with scripted prompts.
module Vara.Engine.Tests.ProfileFileParityTests

open System
open System.IO
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain
open Vara.Domain.Tests
open Vara.Engine
open Vara.Engine.Tests.ParityTests

let private profileOf = ProfileEditorTests.profileOf

/// The same profile in the C# build's types, for its writer.
let private toCSharp (profile: Profile) : Vara.Core.Configuration.Profile =
    let source (source: Source) =
        Vara.Core.Configuration.Source(AbsolutePath.value source.Path, source.Recursive, source.Excludes, source.IncludeGlobs, source.ExcludeGlobs)

    let retention =
        Profile.retention profile
        |> Option.map (fun policy ->
            Vara.Core.Configuration.RetentionPolicy(
                RetentionCount.value policy.KeepDaily,
                RetentionCount.value policy.KeepWeekly,
                RetentionCount.value policy.KeepMonthly,
                RetentionCount.value policy.KeepYearly
            ))
        |> Option.toObj

    let concurrency =
        Profile.concurrency profile
        |> Option.map (fun settings ->
            let limit (value: ConcurrencyLimit option) =
                value |> Option.map (ConcurrencyLimit.value >> Nullable) |> Option.defaultValue (Nullable())

            Vara.Core.Configuration.ConcurrencySettings(limit settings.Scan, limit settings.Transfer))
        |> Option.toObj

    Vara.Core.Configuration.Profile(
        Profile.name profile,
        AbsolutePath.value (Profile.target profile),
        Profile.sources profile |> List.map source |> Array.ofList,
        retention,
        concurrency
    )

let private describeAll (profiles: Profile list) : string list list = profiles |> List.map describeProfile

/// What the F# writer saves, as the C# loader reads it.
let private loadedByCSharp (profiles: Profile list) : string list list =
    let path = Path.Combine(Path.GetTempPath(), $"vara-parity-{Guid.NewGuid():N}.yml")

    try
        match ConfigFile.save path profiles with
        | Ok() -> ()
        | Error error -> failwith (ConfigWriteError.render error)

        Vara.Infrastructure.Configuration.YamlProfileConfigLoader().LoadProfiles path
        |> Seq.map describeCSharpProfile
        |> List.ofSeq
    finally
        File.Delete path

/// What the C# writer saves, as the F# loader reads it.
let private loadedByFSharp (profiles: Profile list) : Result<string list list, string> =
    let path = Path.Combine(Path.GetTempPath(), $"vara-parity-{Guid.NewGuid():N}.yml")

    try
        Vara.Infrastructure.Configuration.YamlProfileConfigWriter().WriteProfiles(profiles |> List.map toCSharp |> Array.ofList, path)

        ConfigFile.load path |> Result.map describeAll |> Result.mapError ConfigError.render
    finally
        File.Delete path

let private source (path: string) (recursive: bool) (excludes: string list) (includes: string list) (excludeGlobs: string list) : ConfigNode =
    let list key (values: string list) =
        if values.IsEmpty then [] else [ key, ConfigNode.Sequence(values |> List.map ConfigNode.Scalar) ]

    ConfigNode.Mapping(
        [ "path", ConfigNode.Scalar path; "recursive", ConfigNode.Scalar(if recursive then "true" else "false") ]
        @ list "exclude" excludes
        @ list "include_globs" includes
        @ list "exclude_globs" excludeGlobs
    )

/// Profiles with every optional field set, with none, with several sources, and with unusual characters in names, paths and
/// globs (a quote, a colon, a hash, a bracket, a comma, spaces, a letter with an accent, words YAML reads as other types).
let private corpus: (string * Profile list) list =
    let unusual =
        let node =
            ConfigNode.Mapping
                [ "name", ConfigNode.Scalar "it's: a #name, [x]"
                  "target", ConfigNode.Scalar "C:\\backup\\caf\u00e9 {b}\\a#b"
                  "sources",
                  ConfigNode.Sequence
                      [ source @"C:\data\k=v, a b" false [ "*.tmp"; "[ab]*"; "#note"; "k: v" ] [ "**/*.md"; "'q'"; "\"dq\"" ] [ "- x"; "yes"; "123"; "true"; "null" ]
                        source "C:\\data\\a,b\u00e9" true [] [] [] ] ]

        match ProfileValidation.validateProfiles [ node ] with
        | Ok profiles -> profiles
        | Error error -> failwith (ConfigError.render error)

    let everything = ProfileWriterTests.everything

    [ "every optional field set", [ everything ]
      "no optional field", [ profileOf "bare" @"C:\backup\bare" [ @"C:\data\bare" ] ]
      "several sources", [ profileOf "many" @"C:\backup\many" [ @"C:\data\a"; @"C:\data\b"; @"C:\data\c" ] ]
      "several profiles", [ profileOf "one" @"C:\backup\one" [ @"C:\data\one" ]; everything; profileOf "two" @"C:\backup\two" [ @"C:\data\two" ] ]
      "unusual characters", unusual
      "no profiles", [] ]

// Both directions.

[<Fact>]
let ``profiles written by the F# writer load in the C# loader with the same fields`` () =
    for name, profiles in corpus do
        Assert.True((loadedByCSharp profiles = describeAll profiles), name)

[<Fact>]
let ``a file written by the C# writer loads in the F# loader with the same profiles`` () =
    for name, profiles in corpus do
        Assert.True((loadedByFSharp profiles = Ok(describeAll profiles)), name)

[<Fact>]
let ``both writers produce a file that both loaders read as the same profiles`` () =
    for name, profiles in corpus do
        Assert.True((loadedByFSharp profiles = Ok(loadedByCSharp profiles)), name)

[<Property>]
let ``generated profiles are read back the same through either writer and either loader`` () =
    Prop.forAll (ProfileEditorPropertyTests.Generators.arbitrary ProfileEditorPropertyTests.Generators.profiles) (fun profiles ->
        let expected = describeAll profiles
        loadedByCSharp profiles = expected && loadedByFSharp profiles = Ok expected)
