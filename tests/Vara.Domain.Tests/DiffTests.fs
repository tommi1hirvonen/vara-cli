/// Scenario and property tests for change classification and deletion detection.
module Vara.Domain.Tests.DiffTests

open Xunit
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain
open Vara.Domain.Tests.Fixtures

let private deletedPaths (result: DiffResult) : string list =
    result.Deleted |> List.map (fun struct (path, _) -> MirrorPath.value path)

module ``Incremental change detection`` =
    [<Fact>]
    let ``No changes since last run`` () =
        let state = tracked [ "C\\a.txt", regular "h1" 10L 1; "C\\b.txt", regular "h2" 20L 2 ]
        let scan = [ ScanEntry.File(file "C\\a.txt" 10L 1); ScanEntry.File(file "C\\b.txt" 20L 2) ]
        let result = Diff.run state scan
        Assert.Empty result.Pending
        Assert.Empty result.Deleted

    [<Fact>]
    let ``File's casing changes without content changing`` () =
        let state = tracked [ ("C\\Photo.JPG", regular "h1" 10L 1) ]
        let result = Diff.run state [ ScanEntry.File(file "C\\photo.jpg" 10L 1) ]
        Assert.Empty result.Pending
        Assert.Empty result.Deleted

    [<Fact>]
    let ``A different size or time is a change, a new path is an add`` () =
        let state = tracked [ "C\\a.txt", regular "h1" 10L 1; "C\\b.txt", regular "h2" 20L 2 ]

        let scan =
            [ ScanEntry.File(file "C\\a.txt" 11L 1)
              ScanEntry.File(file "C\\b.txt" 20L 3)
              ScanEntry.File(file "C\\c.txt" 5L 1) ]

        let result = Diff.run state scan

        match result.Pending with
        | [ PendingChange.Change a; PendingChange.Change b; PendingChange.Add c ] ->
            Assert.Equal("C\\a.txt", MirrorPath.value a.FilePath)
            Assert.Equal("C\\b.txt", MirrorPath.value b.FilePath)
            Assert.Equal("C\\c.txt", MirrorPath.value c.FilePath)
        | other -> Assert.Fail $"unexpected pending changes: {List.length other}"

    [<Fact>]
    let ``Links are recorded once and never diffed as content`` () =
        let state = tracked [ "C\\l", TrackedState.Linked(ValueSome "x"); "C\\f", regular "h1" 1L 1 ]

        let result =
            Diff.run
                state
                [ ScanEntry.Link(link "C\\l" (ValueSome "y")) // already a link: unchanged whatever the target
                  ScanEntry.Link(link "C\\f" (ValueSome "z")) // a file replaced by a link
                  ScanEntry.Link(link "C\\n" ValueNone) ] // new

        match result.Pending with
        | [ PendingChange.Link replaced; PendingChange.Link fresh ] ->
            Assert.Equal("C\\f", MirrorPath.value replaced.LinkPath)
            Assert.Equal("C\\n", MirrorPath.value fresh.LinkPath)
        | other -> Assert.Fail $"unexpected pending changes: {List.length other}"

        Assert.Empty result.Deleted

    [<Fact>]
    let ``A link replaced by a file is a change`` () =
        let state = tracked [ ("C\\l", TrackedState.Linked ValueNone) ]
        let result = Diff.run state [ ScanEntry.File(file "C\\l" 3L 1) ]
        Assert.True(
            match result.Pending with
            | [ PendingChange.Change _ ] -> true
            | _ -> false
        )

module ``Unreadable files do not abort the run`` =
    let private state =
        tracked
            [ "C\\Users\\john\\a.txt", regular "h1" 1L 1
              "C\\Users\\john2\\b.txt", regular "h2" 1L 1
              "C\\Other\\c.txt", regular "h3" 1L 1 ]

    [<Fact>]
    let ``Permission-denied file encountered during scanning`` () =
        let scan =
            [ ScanEntry.Failure(failure "C\\Users\\john\\a.txt" ScanFailureReason.UnreadableEntry)
              ScanEntry.File(file "C\\Users\\john2\\b.txt" 1L 1)
              ScanEntry.File(file "C\\Other\\c.txt" 1L 1) ]

        let result = Diff.run state scan
        Assert.Empty result.Deleted
        Assert.Equal(1, List.length result.Failures)

    [<Fact>]
    let ``Permission-denied directory encountered during scanning`` () =
        let scan =
            [ ScanEntry.Failure(failure "C\\Users\\john" ScanFailureReason.UnreadableDirectory)
              ScanEntry.File(file "C\\Other\\c.txt" 1L 1) ]

        let result = Diff.run state scan
        // The subtree is hidden; the sibling whose name merely starts the same is a real deletion.
        Assert.Equal<string list>([ "C\\Users\\john2\\b.txt" ], deletedPaths result)

    [<Fact>]
    let ``Configured source path does not exist`` () =
        let scan = [ ScanEntry.Failure(failure "C\\Users" ScanFailureReason.SourceUnavailable) ]
        let result = Diff.run state scan
        Assert.Equal<string list>([ "C\\Other\\c.txt" ], deletedPaths result)

    [<Fact>]
    let ``A failure covers paths whichever separator and casing it uses`` () =
        let scan = [ ScanEntry.Failure(failure "c/users/JOHN" ScanFailureReason.UnreadableDirectory) ]
        let result = Diff.run state scan
        Assert.DoesNotContain("C\\Users\\john\\a.txt", deletedPaths result)

    [<Fact>]
    let ``A path no longer in the scan and under no failure is deleted`` () =
        let result = Diff.run state []

        Assert.Equal<string list>(
            [ "C\\Users\\john\\a.txt"; "C\\Users\\john2\\b.txt"; "C\\Other\\c.txt" ],
            deletedPaths result
        )

module ``Properties of the diff`` =
    let private names = [ "C\\a"; "C\\A"; "C\\b"; "C\\dir\\x"; "C\\dir\\y"; "C\\dir2\\z"; "D\\q" ]
    let private covers = [ "C\\dir"; "c/DIR"; "C\\a"; "D"; "C" ]

    let private scenario =
        gen {
            let! trackedNames = Gen.subListOf names
            let! seenNames = Gen.subListOf names
            let! failureCovers = Gen.subListOf covers
            return trackedNames, seenNames, failureCovers
        }

    [<Property>]
    let ``every tracked path ends up seen, deleted, or hidden by a failure`` () =
        Prop.forAll (Arb.fromGen scenario) (fun (trackedNames, seenNames, failureCovers) ->
            let state = tracked [ for n in List.distinctBy (fun (n: string) -> n.ToUpperInvariant()) trackedNames -> n, regular "h" 1L 1 ]

            let scan =
                [ for n in seenNames -> ScanEntry.File(file n 1L 1)
                  for c in failureCovers -> ScanEntry.Failure(failure c ScanFailureReason.UnreadableDirectory) ]

            let result = Diff.run state scan
            let deleted = deletedPaths result |> List.map _.ToUpperInvariant() |> Set.ofList
            let seen = seenNames |> List.map _.ToUpperInvariant() |> Set.ofList

            let hidden (path: string) =
                let p = path.Replace('\\', '/').ToUpperInvariant()

                failureCovers
                |> List.exists (fun c ->
                    let c = c.Replace('\\', '/').ToUpperInvariant()
                    p = c || p.StartsWith(c + "/"))

            state.Keys
            |> Seq.forall (fun path ->
                let key = path.ToUpperInvariant()
                let seenHere = seen.Contains key
                let deletedHere = deleted.Contains key
                // exactly one fate: deleted excludes both seen and hidden
                (seenHere || hidden path || deletedHere) && not (deletedHere && (seenHere || hidden path))))
