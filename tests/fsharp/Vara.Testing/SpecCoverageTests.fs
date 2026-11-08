module Vara.Testing.SpecCoverageTests

open System.Reflection
open Xunit
open Vara.Testing.SpecCoverage

/// Sample tests that the coverage check finds by reflection.
module SampleTests =
    module ``Sample requirement`` =
        [<Fact>]
        let ``Covered scenario`` () = ()

    module ``Other requirement`` =
        [<Fact>]
        let ``Repeated title`` () = ()

    module ``Third requirement`` =
        [<Fact>]
        let ``Repeated title`` () = ()

let private sampleSpec =
    """# sample Specification

### Requirement: Sample requirement
Prose.

#### Scenario: Covered scenario
- **WHEN** a
- **THEN** b

#### Scenario: Uncovered scenario
- **WHEN** c

### Requirement: Other requirement

#### Scenario: Repeated title
- **WHEN** d

### Requirement: Third requirement

#### Scenario: Repeated title
- **WHEN** e

#### Scenario: Repeated title only here
- **WHEN** f
"""

let private assembly = [ Assembly.GetExecutingAssembly() ]

[<Fact>]
let ``parseSpec pairs each scenario with its requirement`` () =
    let expected =
        [ { Requirement = "Sample requirement"; Scenario = "Covered scenario" }
          { Requirement = "Sample requirement"; Scenario = "Uncovered scenario" }
          { Requirement = "Other requirement"; Scenario = "Repeated title" }
          { Requirement = "Third requirement"; Scenario = "Repeated title" }
          { Requirement = "Third requirement"; Scenario = "Repeated title only here" } ]

    Assert.Equal<ScenarioId list>(expected, parseSpec sampleSpec)

[<Fact>]
let ``a scenario with a test of the same name is covered`` () =
    let missing = missingTests [] assembly sampleSpec
    Assert.DoesNotContain({ Requirement = "Sample requirement"; Scenario = "Covered scenario" }, missing)

[<Fact>]
let ``a scenario without a test is reported`` () =
    let missing = missingTests [] assembly sampleSpec
    Assert.Contains({ Requirement = "Sample requirement"; Scenario = "Uncovered scenario" }, missing)

[<Fact>]
let ``a title repeated under two requirements is matched per requirement`` () =
    let spec =
        """### Requirement: Other requirement
#### Scenario: Repeated title
### Requirement: Sample requirement
#### Scenario: Repeated title
"""

    let missing = missingTests [] assembly spec
    Assert.Equal<ScenarioId list>([ { Requirement = "Sample requirement"; Scenario = "Repeated title" } ], missing)

[<Fact>]
let ``an excluded scenario is not reported`` () =
    let uncovered = { Requirement = "Sample requirement"; Scenario = "Uncovered scenario" }
    let missing = missingTests [ uncovered ] assembly sampleSpec
    Assert.DoesNotContain(uncovered, missing)
