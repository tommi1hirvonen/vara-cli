/// Tests of the configuration file and profile resolution. Each test is named after its scenario in
/// openspec/specs/profile-config/spec.md and sits in a module named after its requirement.
module Vara.Engine.Tests.ConfigurationTests

open System
open System.IO
open Xunit
open Vara.Domain
open Vara.Engine

/// Runs the action in a new empty directory and removes it afterwards.
let withScratch (action: string -> unit) : unit =
    let directory = Path.Combine(Path.GetTempPath(), "vara-tests-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory directory |> ignore

    try
        action directory
    finally
        Directory.Delete(directory, true)

let private writeConfig (directory: string) (text: string) : string =
    let configPath = Path.Combine(directory, "profiles.yml")
    File.WriteAllText(configPath, text)
    configPath

/// A profile root: a directory that holds `.vara\profile.db`.
let private makeTargetRoot (directory: string) : unit =
    Directory.CreateDirectory(Path.Combine(directory, ".vara")) |> ignore
    File.WriteAllText(Path.Combine(directory, ".vara", "profile.db"), "")

let private expectError (expected: ConfigError) (outcome: Result<'T, ConfigError>) : unit =
    match outcome with
    | Error error -> Assert.Equal(expected, error)
    | Ok _ -> Assert.Fail "expected an error"

let private expectOk (outcome: Result<'T, ConfigError>) : 'T =
    match outcome with
    | Ok value -> value
    | Error error ->
        Assert.Fail(ConfigError.render error)
        failwith "unreachable"

module ``Profile configuration file location`` =
    [<Fact>]
    let ``Default location used`` () =
        let expected = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".vara", "profiles.yml")
        Assert.Equal(expected, ConfigFile.locate None)
        Assert.Equal(@"C:\other\p.yml", ConfigFile.locate (Some @"C:\other\p.yml"))

    [<Fact>]
    let ``Configuration file missing`` () =
        withScratch (fun directory ->
            let missing = Path.Combine(directory, "profiles.yml")
            let outcome = ConfigFile.load missing
            expectError (ConfigError.FileNotFound missing) outcome

            match outcome with
            | Error error -> Assert.Contains(missing, ConfigError.render error)
            | Ok _ -> ())

module ``Malformed configuration file detection`` =
    let private parse text = YamlConfig.parse "p.yml" text

    [<Fact>]
    let ``Configuration file contains a YAML syntax error`` () =
        match parse "profiles:\n  - name: \"unclosed\n" with
        | Error(ConfigError.Malformed("p.yml", reason)) -> Assert.StartsWith("could not be parsed as YAML - ", reason)
        | other -> Assert.Fail $"expected a malformed error, got {other}"

    [<Fact>]
    let ``Configuration file root is not a mapping`` () =
        for text in [ "- a\n- b\n"; "just text\n" ] do
            expectError (ConfigError.Malformed("p.yml", "the document root must be a mapping")) (parse text)

    [<Fact>]
    let ``"profiles" key present but not a list`` () =
        for text in [ "profiles:\n  name: x\n"; "profiles: text\n" ] do
            expectError (ConfigError.Malformed("p.yml", "'profiles' must be a list")) (parse text)

    [<Fact>]
    let ``Empty configuration file treated as zero profiles`` () =
        for text in [ ""; "# only a comment\n" ] do
            Assert.Equal<ConfigNode list>([], expectOk (parse text))

    [<Fact>]
    let ``Configuration file without a "profiles" key treated as zero profiles`` () =
        Assert.Equal<ConfigNode list>([], expectOk (parse "other: 1\n"))

module ``Profile selection by name`` =
    let private config =
        "profiles:\n  - name: Work\n    target: C:\\Backups\\work\n    sources:\n      - path: C:\\Data\\work\n"

    [<Fact>]
    let ``Unknown profile name`` () =
        withScratch (fun directory ->
            let configPath = writeConfig directory config
            expectError (ConfigError.UnknownProfile "home") (ProfileResolution.forProfile configPath "home"))

    [<Fact>]
    let ``Profile name omitted, working directory inside a profile's target root`` () =
        withScratch (fun directory ->
            makeTargetRoot directory
            let inside = Directory.CreateDirectory(Path.Combine(directory, "docs", "deep")).FullName
            let missingConfig = Path.Combine(directory, "no-such-profiles.yml")

            match expectOk (ProfileResolution.forBrowsing missingConfig None inside) with
            | Resolution.Discovered found ->
                Assert.Equal(directory, AbsolutePath.value found.Root)
                Assert.Equal(Path.GetFileName directory, found.Name)
            | Resolution.Configured _ -> Assert.Fail "expected a target root found from the working directory")

    [<Fact>]
    let ``Profile name omitted, working directory outside any profile's target root`` () =
        withScratch (fun directory ->
            expectError ConfigError.ProfileNameRequired (ProfileResolution.forBrowsing "unused.yml" None directory))

    [<Fact>]
    let ``Profile name given explicitly while inside a profile's target root`` () =
        withScratch (fun directory ->
            makeTargetRoot directory
            let configPath = writeConfig directory config

            match expectOk (ProfileResolution.forBrowsing configPath (Some "work") directory) with
            | Resolution.Configured profile -> Assert.Equal("Work", Profile.name profile)
            | Resolution.Discovered _ -> Assert.Fail "expected the profile from the configuration file")
