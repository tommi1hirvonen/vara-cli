module Vara.Engine.Tests.SpecCoverageTests

open System
open System.IO
open System.Reflection
open Xunit
open Vara.Testing

let private specPath () : Result<string, string> =
    let rec search (directory: DirectoryInfo) : Result<string, string> =
        let candidate = Path.Combine(directory.FullName, "openspec", "specs", "profile-config", "spec.md")

        if File.Exists candidate then Ok candidate
        else
            match Option.ofObj directory.Parent with
            | Some parent -> search parent
            | None -> Error "openspec/specs/profile-config/spec.md not found above the test directory"

    search (DirectoryInfo AppContext.BaseDirectory)

/// Owned by fsharp-retention-and-integrity: it needs the prune command.
let private ownedElsewhere: SpecCoverage.ScenarioId list =
    [ { Requirement = "Retention policy configuration"
        Scenario = "Profile without a retention policy" } ]

/// The tests of this change live in the domain and engine test assemblies.
let private testAssemblies () : Assembly list =
    [ Assembly.Load "Vara.Domain.Tests"; Assembly.GetExecutingAssembly() ]

[<Fact>]
let ``every owned profile-config scenario has a test`` () =
    match specPath () with
    | Error message -> Assert.Fail message
    | Ok path ->
        let missing = SpecCoverage.missingTests ownedElsewhere (testAssemblies ()) (File.ReadAllText path)
        Assert.True(missing.IsEmpty, "Scenarios without a test:\n" + String.Join("\n", missing |> List.map SpecCoverage.describe))
