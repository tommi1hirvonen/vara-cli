/// Checks that every scenario of an OpenSpec spec has a test of the same name.
/// A test method is named after the scenario title and sits in a module named after the requirement title.
module Vara.Testing.SpecCoverage

open System
open System.Reflection

type ScenarioId = { Requirement: string; Scenario: string }

/// F# cannot spell some characters in a module name (a `+`, as in "Ctrl+C", or a `/`, as in "date/time"), so names are
/// compared without them. A backtick, as in "`--snapshot`", cannot be spelled either.
/// The runtime also escapes a comma in a type's name as `\,`, which is read back as the comma it stands for.
let private comparable (id: ScenarioId) : ScenarioId =
    let normalize (name: string) = name.Replace(@"\,", ",").Replace("+", "").Replace("/", "").Replace("`", "")

    { Requirement = normalize id.Requirement
      Scenario = normalize id.Scenario }

let private requirementPrefix = "### Requirement:"
let private scenarioPrefix = "#### Scenario:"

let private titleAfter (prefix: string) (line: string) : string =
    line.Substring(prefix.Length).Trim()

/// The requirement and scenario headings of a spec, in file order.
let parseSpec (specText: string) : ScenarioId list =
    let step (requirement: string option, found: ScenarioId list) (line: string) =
        let line = line.Trim()

        if line.StartsWith requirementPrefix then
            Some(titleAfter requirementPrefix line), found
        elif line.StartsWith scenarioPrefix then
            match requirement with
            | Some requirement ->
                Some requirement, { Requirement = requirement; Scenario = titleAfter scenarioPrefix line } :: found
            | None -> requirement, found
        else
            requirement, found

    specText.Split '\n' |> Array.fold step (None, []) |> snd |> List.rev

let private isTestAttribute (attribute: Attribute) : bool =
    match attribute.GetType().Name with
    | "FactAttribute"
    | "TheoryAttribute"
    | "PropertyAttribute" -> true
    | _ -> false

let private testsOf (declaringType: Type) : ScenarioId list =
    declaringType.GetMethods(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly)
    |> Seq.filter (fun method -> method.GetCustomAttributes true |> Seq.cast<Attribute> |> Seq.exists isTestAttribute)
    |> Seq.map (fun method -> comparable { Requirement = declaringType.Name; Scenario = method.Name })
    |> List.ofSeq

/// The requirement and scenario names that the assemblies' test methods spell out.
let testedScenarios (assemblies: Assembly list) : Set<ScenarioId> =
    assemblies |> Seq.collect _.GetTypes() |> Seq.collect testsOf |> Set.ofSeq

/// The scenarios of the spec that have no test, leaving out the ones another change owns.
let missingTests (excluded: ScenarioId list) (assemblies: Assembly list) (specText: string) : ScenarioId list =
    let tested = testedScenarios assemblies

    parseSpec specText
    |> List.filter (fun scenario ->
        not (List.contains scenario excluded) && not (Set.contains (comparable scenario) tested))

let describe (scenario: ScenarioId) : string =
    $"{scenario.Requirement} / {scenario.Scenario}"
