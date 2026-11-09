/// The F# backup engine against the recorded runs of the C# engine. Each scenario of `EngineScenarios` runs on the F# engine,
/// and the trace of what each run reported and what the target held afterwards is compared with the recorded trace.
module Vara.Engine.Tests.EngineCorpusTests

open System.IO
open Xunit
open Vara.Engine.Tests.EngineScenarios
open Vara.Testing

let private area = "engine"

[<Fact>]
let ``every engine scenario is recorded`` () =
    let recorded = Corpus.names area |> List.map Path.GetFileNameWithoutExtension |> List.sort
    Assert.Equal<string list>(scenarios |> List.map (fun scenario -> scenario.Name) |> List.sort, recorded)

[<Fact>]
let ``the F# engine leaves the recorded manifest, mirror and blobs in every scenario`` () =
    let compared =
        scenarios |> List.map (fun scenario -> scenario.Name, Corpus.read area (scenario.Name + ".txt"), observe runFs scenario)

    Corpus.assertSame compared

[<Fact>]
let ``the F# engine continues a target the C# engine wrote without re-transfers`` () =
    using (fun rig ->
        LegacyTarget.legacySources rig
        LegacyTarget.restore rig

        let changes (observed: Observed) = observed.Added + observed.Changed + observed.Moved + observed.Deleted
        Assert.Equal(0, changes (returned (runFs rig plain)))

        // And it carries on from there: a change is picked up.
        write rig "c.txt" "gamma" |> ignore
        Assert.Equal(1, (returned (runFs rig plain)).Added)

        // A moved file keeps its history across the implementations.
        File.Move(Path.Combine(rig.Source, "a.txt"), Path.Combine(rig.Source, "moved.txt"))
        Assert.Equal(1, (returned (runFs rig plain)).Moved)
        Assert.Equal(0, changes (returned (runFs rig plain))))
