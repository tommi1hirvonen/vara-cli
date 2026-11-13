/// Configuration files recorded as golden YAML (`NN-name.yml`) with the profiles they describe (`NN-name.profiles`). The
/// loader must read each file as the recorded profiles, and the writer's output for those profiles must load back to the same profiles through `ofProfiles`, `render`, `parse` and
/// `validateProfiles`. The files are compared as profile descriptions, never as text.
module Vara.Engine.Tests.WriterCorpusTests

open System.IO
open Xunit
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain
open Vara.Domain.Tests
open Vara.Engine
open Vara.Testing

let private area = "writer"

let private golden: string list =
    Corpus.names area |> List.filter (fun name -> name.EndsWith ".yml") |> List.map Path.GetFileNameWithoutExtension

let private recordedCases = 6

let private loadGolden (directory: string) (id: string) : Result<Profile list, ConfigError> =
    let configPath = Path.Combine(directory, id + ".yml")
    File.WriteAllText(configPath, Corpus.readRaw area (id + ".yml"))
    ConfigFile.load configPath

let private described (profiles: Result<Profile list, ConfigError>) : string =
    profiles
    |> Result.map (List.map ProfileDescription.describeProfile)
    |> Result.mapError ConfigError.render
    |> ProfileDescription.resultText ""

/// The writer's way from profiles to profiles, without touching the file system.
let private roundTrip (profiles: Profile list) : Result<Profile list, ConfigError> =
    ProfileNodes.ofProfiles profiles
    |> YamlConfig.render
    |> YamlConfig.parse "profiles.yml"
    |> Result.bind ProfileValidation.validateProfiles

[<Fact>]
let ``the loader reads every golden file as the recorded profiles`` () =
    ConfigurationTests.withScratch (fun directory ->
        let cases = golden |> List.map (fun id -> id, Corpus.read area (id + ".profiles"), described (loadGolden directory id))
        Corpus.assertSame cases)

[<Fact>]
let ``the writer's output loads back to the recorded profiles`` () =
    ConfigurationTests.withScratch (fun directory ->
        let cases =
            golden
            |> List.map (fun id ->
                let profiles = loadGolden directory id |> Result.defaultWith (fun error -> failwith (ConfigError.render error))
                id, Corpus.read area (id + ".profiles"), described (roundTrip profiles))

        Corpus.assertSame cases)

[<Fact>]
let ``the writer corpus keeps its cases and reads no profiles as none`` () =
    Assert.Equal(recordedCases, golden.Length)
    Assert.Equal(recordedCases, Corpus.names area |> List.filter (fun name -> name.EndsWith ".profiles") |> List.length)
    let none = (Corpus.read area "06-no-profiles.profiles").TrimEnd '\n'
    Assert.Equal("ok", none)

[<Property>]
let ``generated profiles are read back the same through the writer and loader`` () =
    Prop.forAll (ProfileEditorPropertyTests.Generators.arbitrary ProfileEditorPropertyTests.Generators.profiles) (fun profiles ->
        described (roundTrip profiles) = described (Ok profiles))
