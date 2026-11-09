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

/// The scenarios of a spec that have no test, leaving out those that `excluded` picks from the spec's scenarios.
let private missingIn (capability: string) (excluded: SpecCoverage.ScenarioId list -> SpecCoverage.ScenarioId list) : string list =
    match specPath capability with
    | Error message -> [ message ]
    | Ok path ->
        let text = File.ReadAllText path

        SpecCoverage.missingTests (excluded (SpecCoverage.parseSpec text)) (testAssemblies ()) text
        |> List.map SpecCoverage.describe

/// The scenarios of the named requirements.
let private ofRequirements (names: string list) (scenarios: SpecCoverage.ScenarioId list) : SpecCoverage.ScenarioId list =
    scenarios |> List.filter (fun scenario -> List.contains scenario.Requirement names)

/// The scenarios of every requirement but the named ones.
let private exceptRequirements (names: string list) (scenarios: SpecCoverage.ScenarioId list) : SpecCoverage.ScenarioId list =
    scenarios |> List.filter (fun scenario -> not (List.contains scenario.Requirement names))

/// The `progress-reporting` requirements that a later change owns, excluded by name.
let private progressOwnedElsewhere: string list =
    [ "Count-based progress indication for non-byte-transfer operations" ] // fsharp-retention-and-integrity

/// The `cli-presentation` requirements this change and the one before it own; the others belong to the commands that
/// need them.
let private cliPresentationOwned: string list =
    [ "Outcome severity is visually distinct"
      "Outcome severity determines process exit code"
      "Non-interactive or color-incapable output falls back to plain text"
      "Unclassified exceptions are rendered in the hard-error style"
      "Machine-readable JSON output for the backup command"
      "Bordered widgets use a consistent rounded-corner style"
      "Invalid date/time option input produces a friendly error"
      "Date/time options document an example value in their help text" ]

let private show (missing: string list) : string =
    "Scenarios without a test: " + String.Join(" | ", missing)

[<Fact>]
let ``every owned backup-execution scenario has a test`` () =
    let missing = missingIn "backup-execution" (fun _ -> [])
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned progress-reporting scenario has a test`` () =
    let missing = missingIn "progress-reporting" (ofRequirements progressOwnedElsewhere)
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned cli-presentation scenario has a test`` () =
    let missing = missingIn "cli-presentation" (exceptRequirements cliPresentationOwned)
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned snapshot-history scenario has a test`` () =
    let missing = missingIn "snapshot-history" (fun _ -> [])
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned backup-browsing scenario has a test`` () =
    let missing = missingIn "backup-browsing" (fun _ -> [])
    Assert.True(missing.IsEmpty, show missing)
