module Vara.Engine.Tests.SpecCoverageTests

open System
open System.IO
open System.Reflection
open Xunit
open Vara.Testing

let private specPath (capability: string) : Result<string, string> =
    let rec search (directory: DirectoryInfo) : Result<string, string> =
        let candidate = Path.Combine(directory.FullName, "openspec", "specs", capability, "spec.md")

        if File.Exists candidate then Ok candidate
        else
            match Option.ofObj directory.Parent with
            | Some parent -> search parent
            | None -> Error $"openspec/specs/{capability}/spec.md not found above the test directory"

    search (DirectoryInfo AppContext.BaseDirectory)

/// Owned by fsharp-retention-and-integrity: it needs the prune command.
let private ownedElsewhere: SpecCoverage.ScenarioId list =
    [ { Requirement = "Retention policy configuration"
        Scenario = "Profile without a retention policy" } ]

/// The tests of these changes live in the domain, engine and command test assemblies.
let private testAssemblies () : Assembly list =
    [ Assembly.Load "Vara.Domain.Tests"; Assembly.GetExecutingAssembly(); Assembly.Load "Vara.Cli.Tests" ]

[<Fact>]
let ``every owned profile-config scenario has a test`` () =
    match specPath "profile-config" with
    | Error message -> Assert.Fail message
    | Ok path ->
        let missing = SpecCoverage.missingTests ownedElsewhere (testAssemblies ()) (File.ReadAllText path)
        Assert.True(missing.IsEmpty, "Scenarios without a test:\n" + String.Join("\n", missing |> List.map SpecCoverage.describe))

/// Owned by fsharp-backup-presentation, together with the rest of the backup command's presentation.
let private sleepPreventionRequirements: string list =
    [ "Automatic system sleep is prevented during a real backup"
      "Sleep-prevention request failure does not block backup" ]

[<Fact>]
let ``every owned backup-execution scenario has a test`` () =
    match specPath "backup-execution" with
    | Error message -> Assert.Fail message
    | Ok path ->
        let text = File.ReadAllText path
        let excluded =
            SpecCoverage.parseSpec text
            |> List.filter (fun scenario -> List.contains scenario.Requirement sleepPreventionRequirements)

        let missing = SpecCoverage.missingTests excluded (testAssemblies ()) text
        Assert.True(missing.IsEmpty, "Scenarios without a test:
" + String.Join("
", missing |> List.map SpecCoverage.describe))
