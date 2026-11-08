/// Scenario and property tests for move-candidate selection and plan resolution.
module Vara.Domain.Tests.PlanningTests

open System
open System.Collections.Generic
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain
open Vara.Domain.Tests.Fixtures

let private withQuick (quickHash: string) (scheme: int) (content: FileContent) : FileContent =
    { content with
        Quick = ValueSome { QuickHash = quickHash; QuickScheme = scheme } }

let private candidateList (contents: (string * FileContent) list) =
    contents |> List.map (fun (path, content) -> mirror path, content)

let private hashes (entries: (string * string) list) : Dictionary<string, ContentHash> =
    let map = Dictionary<string, ContentHash>(StringComparer.OrdinalIgnoreCase)

    for path, text in entries do
        map[path] <- hash text

    map

let private resolve (state: (string * TrackedState) list) (scan: ScanEntry list) (full: (string * string) list) =
    let tracked = tracked state
    let diff = Diff.run tracked scan
    let candidates = Planning.moveCandidates diff.Deleted
    Planning.plan tracked diff candidates (hashes full)

let private moveFroms (plan: BackupPlan) : string list =
    plan.Metadata
    |> List.choose (function
        | MetadataOperation.Move move -> Some(MirrorPath.value move.MoveFrom)
        | _ -> None)

module ``Move detection avoids unbounded reads for non-matching candidates`` =
    [<Fact>]
    let ``Same-size, differing-content candidate ruled out cheaply`` () =
        let candidates =
            candidateList [ "C\\old1", withQuick "q1" 1 (content "h1" 10L 1); "C\\old2", withQuick "q2" 1 (content "h2" 10L 1) ]

        Assert.False(Planning.needsFullHash candidates "other")

    [<Fact>]
    let ``Full content match still required to confirm a move`` () =
        let candidates = candidateList [ "C\\old1", withQuick "q1" 1 (content "h1" 10L 1) ]
        Assert.True(Planning.needsFullHash candidates "q1")

        // The bounded read only escalates; the exact hash decides. A prefix match with another full hash is an add.
        let plan =
            resolve
                [ "C\\old1", TrackedState.Regular(withQuick "q1" 1 (content "h1" 10L 1)) ]
                [ ScanEntry.File(file "C\\new" 10L 5) ]
                [ "C\\new", "different" ]

        Assert.Empty(moveFroms plan)
        Assert.Equal(10L, plan.TotalBytes)

    [<Fact>]
    let ``Recorded signature unavailable for a candidate`` () =
        let missing = candidateList [ "C\\old1", content "h1" 10L 1 ]
        let otherScheme = candidateList [ "C\\old1", withQuick "q1" 99 (content "h1" 10L 1) ]
        Assert.True(Planning.needsFullHash missing "anything")
        Assert.True(Planning.needsFullHash otherScheme "q1")

        // One unusable candidate forces the read even when the usable ones are ruled out.
        let mixed =
            candidateList [ "C\\a", withQuick "q1" 1 (content "h1" 10L 1); "C\\b", content "h2" 10L 1 ]

        Assert.True(Planning.needsFullHash mixed "other")

module ``Move and rename detection`` =
    [<Fact>]
    let ``File moved to a new folder`` () =
        let plan =
            resolve
                [ "C\\old\\a.txt", regular "h1" 10L 1 ]
                [ ScanEntry.File(file "C\\new\\a.txt" 10L 5) ]
                [ "C\\new\\a.txt", "h1" ]

        match plan.Metadata with
        | [ MetadataOperation.Move move ] ->
            Assert.Equal("C\\new\\a.txt", MirrorPath.value move.MoveTo)
            Assert.Equal("C\\old\\a.txt", MirrorPath.value move.MoveFrom)
            Assert.True(move.Moved.SourceModifiedAt.Equals(at 5))
        | _ -> Assert.Fail "expected exactly one move"

        Assert.Empty plan.Transfers
        Assert.Equal(0L, plan.TotalBytes)

    [<Fact>]
    let ``Identical candidates are consumed in deleted-path order`` () =
        let plan =
            resolve
                [ "C\\z-old", regular "h1" 10L 1; "C\\a-old", regular "h1" 10L 1 ]
                [ ScanEntry.File(file "C\\n1" 10L 5); ScanEntry.File(file "C\\n2" 10L 5); ScanEntry.File(file "C\\n3" 10L 5) ]
                [ "C\\n1", "h1"; "C\\n2", "h1"; "C\\n3", "h1" ]

        // The manifest's order is z-old, a-old: n1 takes z-old, n2 takes a-old, n3 finds none and is an add.
        Assert.Equal<string list>([ "C\\z-old"; "C\\a-old" ], moveFroms plan)
        Assert.Equal(1, List.length plan.Transfers)
        Assert.Equal(10L, plan.TotalBytes)

    [<Fact>]
    let ``A changed file is never a move and records what it supersedes`` () =
        let plan =
            resolve
                [ "C\\a", regular "h1" 10L 1; "C\\gone", regular "h2" 11L 1 ]
                [ ScanEntry.File(file "C\\a" 11L 2) ]
                [ "C\\a", "h2" ]

        match plan.Transfers with
        | [ { TransferKind = TransferKind.Changed(ValueSome superseded) } ] -> Assert.Equal("h1", ContentHash.value superseded)
        | _ -> Assert.Fail "expected one changed transfer"

        // C\gone stays a deletion; the edit of C\a did not take it.
        Assert.True(plan.Metadata |> List.exists (function MetadataOperation.Delete _ -> true | _ -> false))

    [<Fact>]
    let ``Links are metadata and a replaced file's content is superseded`` () =
        let plan =
            resolve
                [ "C\\f", regular "h1" 10L 1 ]
                [ ScanEntry.Link(link "C\\f" (ValueSome "t")) ]
                []

        match plan.Metadata with
        | [ MetadataOperation.Link op ] -> Assert.Equal("h1", ContentHash.value op.Superseded.Value)
        | _ -> Assert.Fail "expected one link operation"

        Assert.Empty plan.Transfers

    [<Fact>]
    let ``Deleted links are not move candidates`` () =
        let candidates = Planning.moveCandidates [ mirror "C\\l", TrackedState.Linked ValueNone ]
        Assert.Equal(0, candidates.Count)

module ``Properties of planning`` =
    // A few sizes and contents, so that adds often match deletions and several candidates collide.
    let private scenario =
        gen {
            let! deletedCount = Gen.choose (0, 5)
            let! deleted = Gen.listOfLength deletedCount (Gen.zip (Gen.choose (1, 3) |> Gen.map int64) (Gen.choose (1, 3)))
            let! addedCount = Gen.choose (0, 6)
            let! added = Gen.listOfLength addedCount (Gen.zip (Gen.choose (1, 3) |> Gen.map int64) (Gen.choose (1, 3)))
            let! changedSizes = Gen.listOfLength 2 (Gen.choose (1, 50) |> Gen.map int64)
            return deleted, added, changedSizes
        }

    let private build (deleted, added, changedSizes) =
        let state =
            [ for i, (size, h) in List.indexed deleted -> $"C\\del{i}", regular $"h{h}" size 1
              for i, size in List.indexed changedSizes -> $"C\\chg{i}", regular "hc" 100L 1 ]

        let scan =
            [ for i, (size, _) in List.indexed added -> ScanEntry.File(file $"C\\add{i}" size 2)
              for i, size in List.indexed changedSizes -> ScanEntry.File(file $"C\\chg{i}" size 2) ]

        let full = [ for i, (_, h) in List.indexed added -> $"C\\add{i}", $"h{h}" ]
        resolve state scan full, added

    [<Property>]
    let ``no deleted row is consumed twice`` () =
        Prop.forAll (Arb.fromGen scenario) (fun input ->
            let plan, _ = build input
            let froms = moveFroms plan
            let deletes =
                plan.Metadata
                |> List.choose (function
                    | MetadataOperation.Delete d -> Some(MirrorPath.value d.DeletePath)
                    | _ -> None)

            List.length froms = List.length (List.distinct froms)
            && not (froms |> List.exists (fun path -> List.contains path deletes)))

    [<Property>]
    let ``plan totals equal the sum of transfer sizes`` () =
        Prop.forAll (Arb.fromGen scenario) (fun input ->
            let plan, _ = build input
            plan.TotalBytes = (plan.Transfers |> List.sumBy (fun t -> t.Source.Size)))
