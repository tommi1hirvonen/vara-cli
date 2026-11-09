/// Tests of the check's verdict, its fold and its report, on plain data. Each module is named after a requirement of
/// openspec/specs/backup-integrity/spec.md and each test after one of its scenarios.
module Vara.Domain.Tests.IntegrityTests

open Xunit
open Vara.Domain
open Vara.Domain.Tests.Fixtures

/// Folds blobs through the verdict and the fold, as the shell does. `observe` says what was seen of each one.
let private run (quick: bool) (orphaned: string list) (stopped: bool) (blobs: (string * Observation) list) : CheckResult =
    blobs
    |> List.fold
        (fun result (name, observed) ->
            let blob = hash name
            Check.add (fun _ -> [ $"dir\\{name}.txt" ]) blob (Check.verdict quick blob observed) result)
        Check.empty
    |> Check.finish (orphaned |> List.map hash) stopped

let private present (name: string) : Observation = Observation.Present(ValueSome(hash name))

module ``Check command verifies stored content against the manifest`` =
    [<Fact>]
    let ``All referenced content intact`` () =
        let result = run false [] false [ "a", present "a"; "b", present "b" ]
        Assert.Equal(2, result.Checked)
        Assert.False(Check.hasProblems result)
        Assert.Equal(Outcome.Success, Check.outcome result)

    [<Fact>]
    let ``Referenced blob missing from the store`` () =
        let result = run false [] false [ "a", present "a"; "b", Observation.Absent ]
        Assert.Equal<Finding list>([ { Hash = hash "b"; Paths = [ "dir\\b.txt" ]; PathCount = 1 } ], result.Missing)
        Assert.Empty result.Corrupt
        Assert.Equal(Outcome.PartialFailure, Check.outcome result)

    [<Fact>]
    let ``Referenced blob present but corrupted`` () =
        let result = run false [] false [ ("a", Observation.Present(ValueSome(hash "something else"))) ]
        Assert.Equal<Finding list>([ { Hash = hash "a"; Paths = [ "dir\\a.txt" ]; PathCount = 1 } ], result.Corrupt)
        Assert.Empty result.Missing
        Assert.Equal(Outcome.PartialFailure, Check.outcome result)

    [<Fact>]
    let ``Unreferenced content present in the store`` () =
        // An orphan is reported and never a problem: it does not change the outcome.
        let result = run false [ "orphan" ] false [ ("a", present "a") ]
        Assert.Equal<ContentHash list>([ hash "orphan" ], result.Orphaned)
        Assert.False(Check.hasProblems result)
        Assert.Equal(Outcome.Success, Check.outcome result)

    [<Fact>]
    let ``Profile contains a tracked symlink or junction`` () =
        // A link row has no content hash, so it is never among the blobs that reach the verdict; the engine's query
        // leaves it out (tested on a real manifest). What reaches the fold is only real content, all intact.
        let result = run false [] false [ ("a", present "a") ]
        Assert.Empty result.Missing
        Assert.Empty result.Corrupt
        Assert.Equal(Outcome.Success, Check.outcome result)

    [<Fact>]
    let ``a finding lists at most five paths and counts them all`` () =
        let paths = [ for number in 1..8 -> $"p{number}.txt" ]
        let result = Check.add (fun _ -> paths) (hash "a") Verdict.Missing Check.empty
        let finding = List.exactlyOne result.Missing
        Assert.Equal<string list>(List.truncate 5 paths, finding.Paths)
        Assert.Equal(8, finding.PathCount)

    [<Fact>]
    let ``the paths of an intact blob are never looked up`` () =
        let lookedUp = ref 0

        let result =
            Check.add
                (fun _ ->
                    lookedUp.Value <- lookedUp.Value + 1
                    [])
                (hash "a")
                Verdict.Intact
                Check.empty

        Assert.Equal(0, lookedUp.Value)
        Assert.Equal(1, result.Checked)

module ``Quick mode trades thoroughness for speed`` =
    [<Fact>]
    let ``Quick check skips content re-hashing`` () =
        // A blob that is present and changed is not reported in quick mode; a missing one is, as in the default mode.
        let result =
            run true [] false [ "a", Observation.Present(ValueSome(hash "changed")); "b", Observation.Present ValueNone; "c", Observation.Absent ]

        Assert.Empty result.Corrupt
        Assert.Equal<ContentHash list>([ hash "c" ], result.Missing |> List.map _.Hash)

module ``Check exits with a scriptable status`` =
    [<Fact>]
    let ``Problems found`` () =
        let result = run false [] false [ ("a", Observation.Absent) ]
        let code = Outcome.exitCode (Check.outcome result)
        Assert.NotEqual(0, code)
        // Distinct from the code of a hard error such as a missing profile.
        Assert.NotEqual(Outcome.exitCode Outcome.HardError, code)

    [<Fact>]
    let ``No problems found`` () =
        let result = run false [ "orphan" ] false [ ("a", present "a") ]
        Assert.Equal(0, Outcome.exitCode (Check.outcome result))

module ``Graceful cancellation via CtrlC`` =
    [<Fact>]
    let ``Cancelled run reports problems found before cancellation`` () =
        let result = run false [] true [ ("a", Observation.Absent) ]
        let text = CheckView.report result |> Display.plainLines |> String.concat "\n"
        Assert.Contains("1 missing", text)
        Assert.Contains("Missing:", text)
        Assert.Contains("Check cancelled", text)

    [<Fact>]
    let ``a stopped run with no problems is still reported as stopped`` () =
        let result = run false [] true [ ("a", present "a") ]
        let text = CheckView.report result |> Display.plainLines |> String.concat "\n"
        Assert.Contains("Check cancelled", text)
        // The C# build's code for a stopped check; the spec's "exits successfully" is a recorded gap.
        Assert.Equal(Outcome.PartialFailure, Check.outcome result)
