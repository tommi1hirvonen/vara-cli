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

/// The tests of every spec live in the domain, engine and command test assemblies.
let private testAssemblies () : Assembly list =
    [ Assembly.Load "Vara.Domain.Tests"; Assembly.GetExecutingAssembly(); Assembly.Load "Vara.Cli.Tests" ]

[<Fact>]
let ``every owned profile-config scenario has a test`` () =
    match specPath "profile-config" with
    | Error message -> Assert.Fail message
    | Ok path ->
        let missing = SpecCoverage.missingTests [] (testAssemblies ()) (File.ReadAllText path)
        Assert.True(missing.IsEmpty, "Scenarios without a test:\n" + String.Join("\n", missing |> List.map SpecCoverage.describe))

/// The scenarios of a spec that have no test.
let private missingIn (capability: string) : string list =
    match specPath capability with
    | Error message -> [ message ]
    | Ok path ->
        let text = File.ReadAllText path
        SpecCoverage.missingTests [] (testAssemblies ()) text |> List.map SpecCoverage.describe

let private show (missing: string list) : string =
    "Scenarios without a test: " + String.Join(" | ", missing)

[<Fact>]
let ``every owned backup-execution scenario has a test`` () =
    let missing = missingIn "backup-execution"
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned progress-reporting scenario has a test`` () =
    let missing = missingIn "progress-reporting"
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned cli-presentation scenario has a test`` () =
    let missing = missingIn "cli-presentation"
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned snapshot-history scenario has a test`` () =
    let missing = missingIn "snapshot-history"
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned backup-browsing scenario has a test`` () =
    let missing = missingIn "backup-browsing"
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned retention-pruning scenario has a test`` () =
    let missing = missingIn "retention-pruning"
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned backup-integrity scenario has a test`` () =
    let missing = missingIn "backup-integrity"
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned profile-management scenario has a test`` () =
    let missing = missingIn "profile-management"
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned portable-distribution scenario has a test`` () =
    let missing = missingIn "portable-distribution"
    Assert.True(missing.IsEmpty, show missing)

[<Fact>]
let ``every owned manifest-versioning scenario has a test`` () =
    let missing = missingIn "manifest-versioning"
    Assert.True(missing.IsEmpty, show missing)
