/// The F# loader against the recorded results of the C# loader. A case is a YAML input (`NNN.yml`) and the text of the C#
/// result (`NNN.expected`): the profile lines or the error message, with the file's path as `<config>`. One comparison
/// function serves every case.
module Vara.Engine.Tests.LoaderCorpusTests

open System.IO
open Xunit
open Vara.Testing

let private area = "loader"

/// The number of YAML cases that the loader corpus was recorded from, plus the case with no file.
let private recordedCases = 74

let private inputs: string list = Corpus.names area |> List.filter (fun name -> name.EndsWith ".yml")

/// The F# result for a recorded input, written to a file of its own because the loader reads files.
let private loadInput (directory: string) (name: string) : string =
    let configPath = Path.Combine(directory, name)
    File.WriteAllText(configPath, Corpus.readRaw area name)
    ProfileDescription.loadText configPath

[<Fact>]
let ``the F# loader gives the recorded profiles or the recorded error message for every input`` () =
    ConfigurationTests.withScratch (fun directory ->
        let cases =
            inputs
            |> List.map (fun name ->
                let id = Path.GetFileNameWithoutExtension name
                id, Corpus.read area (id + ".expected"), loadInput directory name)

        Corpus.assertSame cases)

[<Fact>]
let ``the F# loader reports a missing file with the recorded message`` () =
    ConfigurationTests.withScratch (fun directory ->
        let missing = Path.Combine(directory, "missing", "profiles.yml")
        let actual = ProfileDescription.loadText missing
        Corpus.assertSame [ ("missing-file", Corpus.read area "missing-file.expected", actual) ])

[<Fact>]
let ``the loader corpus keeps all its cases and exercises both successes and errors`` () =
    let expected = Corpus.names area |> List.filter (fun name -> name.EndsWith ".expected")
    Assert.Equal(recordedCases, expected.Length)
    Assert.Equal(recordedCases - 1, inputs.Length)
    let outcomes = expected |> List.map (fun name -> (Corpus.read area name).StartsWith "ok")
    Assert.Contains(true, outcomes)
    Assert.Contains(false, outcomes)
