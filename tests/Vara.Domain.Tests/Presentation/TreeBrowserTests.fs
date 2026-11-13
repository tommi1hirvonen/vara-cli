/// Tests of the interactive change tree's state machine and frames. Each module is named after a requirement of the
/// interactive-change-tree change (snapshot-history and cli-presentation), and each test after one of its scenarios.
module Vara.Domain.Tests.TreeBrowserTests

open Xunit
open Vara.Domain

let private entry (path: string) (kind: ChangeKind) : ChangeEntry =
    { ChangedPath = path
      ChangedKind = kind
      MovedFrom = ValueNone }

let private changes =
    [ entry "C\\a\\one.txt" ChangeKind.Added
      entry "C\\a\\b\\two.txt" ChangeKind.Changed
      entry "C\\a\\b\\c\\three.txt" ChangeKind.Deleted
      { ChangedPath = "C\\a\\moved.txt"
        ChangedKind = ChangeKind.Moved
        MovedFrom = ValueSome "C\\z\\old.txt" } ]

let private header: Display = [ Line.Text(Tone.Plain, "Snapshot #1") ]

let private session (files: bool) (depth: int voption) : TreeState =
    TreeBrowser.create header "C" Set.empty depth (History.rollupEntries "C" files changes)

let private press (keys: Key list) (state: TreeState) : TreeState =
    keys |> List.fold (fun state key -> TreeBrowser.step key state) state

let private text (line: Line) : string =
    match line with
    | Line.Spans spans -> spans |> List.map _.Text |> String.concat ""
    | Line.Text(_, value) -> value
    | _ -> ""

let private frameLines (height: int) (state: TreeState) : string list =
    TreeBrowser.frame height state |> snd |> List.map text

/// The tree rows only: no header, no status line.
let private treeRows (state: TreeState) : string list =
    let lines = frameLines 100 state
    lines |> List.skip 1 |> List.take (lines.Length - 2)

let private cursorRow (state: TreeState) : string =
    treeRows state |> List.find (fun row -> row.StartsWith "> ")

let private countsOf (row: string) : string = row.Substring(row.IndexOf '(')

module ``Interactive snapshot change tree`` =
    [<Fact>]
    let ``Navigating with the arrow keys`` () =
        let state = session false (ValueSome 3) |> press [ Key.Down; Key.Down; Key.Up ]
        Assert.Contains("a (", cursorRow state)

    [<Fact>]
    let ``Expanding a collapsed directory`` () =
        let state = session false (ValueSome 0)
        Assert.Single(treeRows state) |> ignore
        let opened = state |> press [ Key.Right ]
        Assert.Equal(2, (treeRows opened).Length)
        Assert.Contains("[+] a", List.last (treeRows opened))

    [<Fact>]
    let ``Moving into an expanded directory`` () =
        let before = session false ValueNone
        let state = before |> press [ Key.Right ]
        Assert.Contains("a (", cursorRow state)
        Assert.Equal<string list>(treeRows before |> List.map _.Substring(2), treeRows state |> List.map _.Substring(2))

    [<Fact>]
    let ``Collapsing a directory`` () =
        let state = session false ValueNone |> press [ Key.Left ]
        Assert.Single(treeRows state) |> ignore

    [<Fact>]
    let ``Moving to the parent`` () =
        let state = session false ValueNone |> press [ Key.Down; Key.Left ]
        Assert.Contains("[-] C", cursorRow state)
        Assert.Equal(2, (treeRows state).Length)

    [<Fact>]
    let ``Left on the scope root`` () =
        let state = session false (ValueSome 0) |> press [ Key.Left; Key.Left ]
        Assert.Single(treeRows state) |> ignore
        Assert.Contains("[+] C", cursorRow state)

    [<Fact>]
    let ``Counts are the same collapsed or expanded`` () =
        let collapsed = session false ValueNone
        let find (state: TreeState) = treeRows state |> List.find (fun row -> row.Contains " a (")
        let expanded = collapsed |> press [ Key.Down; Key.Right ]
        Assert.Equal<string>(countsOf (find collapsed), countsOf (find expanded))
        Assert.Contains("+1 ~1 ->1 -1", countsOf (find collapsed))

    [<Fact>]
    let ``Initial expansion from depth`` () =
        let shown = treeRows (session false (ValueSome 2))
        Assert.Equal(3, shown.Length)
        Assert.Contains("[+] b", List.last shown)
        Assert.DoesNotContain(shown, fun row -> row.Contains " c (")

    [<Fact>]
    let ``Default initial expansion`` () =
        let shown = treeRows (session false ValueNone)
        Assert.Equal(2, shown.Length)
        Assert.Contains("[+] a", List.last shown)

    [<Fact>]
    let ``Expanding everything`` () =
        let shown = treeRows (session false ValueNone |> press [ Key.ExpandAll ])
        Assert.Equal(4, shown.Length)
        Assert.Contains("c (", List.last shown)

    [<Fact>]
    let ``Jumping to the ends`` () =
        let state = session false ValueNone |> press [ Key.ExpandAll; Key.End ]
        Assert.Contains("c (", cursorRow state)
        Assert.Contains("C (", cursorRow (state |> press [ Key.Home ]))

    [<Fact>]
    let ``Showing files in the interactive tree`` () =
        let shown = treeRows (session true ValueNone |> press [ Key.ExpandAll ])
        Assert.Contains(shown, fun row -> row.Contains "one.txt - Added")
        Assert.Contains(shown, fun row -> row.Contains "moved.txt - Moved from C\\z\\old.txt")

    [<Fact>]
    let ``Files are leaves`` () =
        let onFile =
            session true ValueNone
            |> press [ Key.ExpandAll ]
            |> fun state -> (treeRows state |> List.findIndex (fun row -> row.Contains "one.txt")) |> fun index -> press (List.replicate index Key.Down) state

        Assert.Contains("one.txt", cursorRow onFile)
        Assert.Equal<string list>(treeRows onFile, treeRows (onFile |> press [ Key.Right ]))

    [<Fact>]
    let ``Ending the session`` () =
        Assert.True((session false ValueNone |> press [ Key.Quit ]).Finished)
        Assert.False((session false ValueNone).Finished)

    [<Fact>]
    let ``Unassigned keys`` () =
        let state = session false ValueNone
        Assert.Equal(state, state |> press [ Key.Other ])

    [<Fact>]
    let ``The cursor falls back to the nearest shown ancestor`` () =
        let expanded = session false ValueNone |> press [ Key.ExpandAll; Key.End ]
        let collapsedAbove = { expanded with Expanded = expanded.Expanded.Remove "c\\a" }
        let settled = collapsedAbove |> press [ Key.Other ]
        Assert.Contains("a (", cursorRow settled)

module ``Interactive tree scrolls within the terminal`` =
    let private deep = session false ValueNone |> press [ Key.ExpandAll ]

    [<Fact>]
    let ``Cursor moves past the last visible row`` () =
        // A header line, two tree rows, and the status line: four lines in all.
        let state = deep |> press [ Key.Down; Key.Down ]
        let fitted, frame = TreeBrowser.frame 4 state
        let lines = frame |> List.map text
        Assert.Equal("Snapshot #1", lines.Head)
        Assert.Equal(4, lines.Length)
        Assert.StartsWith("> ", lines[2])
        Assert.Equal(1, fitted.Offset)

    [<Fact>]
    let ``Indicating hidden rows`` () =
        let state = deep |> press [ Key.Down ]
        let status = frameLines 4 state |> List.last
        Assert.Contains("more below", status)
        let scrolled = deep |> press [ Key.Down; Key.Down; Key.Down ]
        Assert.Contains("more above", frameLines 4 scrolled |> List.last)

    [<Fact>]
    let ``Resizing the terminal`` () =
        let fitted, _ = TreeBrowser.frame 100 (deep |> press [ Key.End ])
        let _, small = TreeBrowser.frame 3 fitted
        Assert.Contains(small |> List.map text, fun line -> line.StartsWith "> ")

    [<Fact>]
    let ``Collapsing near the end of the list`` () =
        let atEnd, _ = TreeBrowser.frame 5 (deep |> press [ Key.End ])
        let collapsed = atEnd |> press [ Key.Up; Key.Left ]
        let _, frame = TreeBrowser.frame 5 collapsed
        // The collapse shortened the list to three rows; the window shows all of them, not a blank tail.
        Assert.Equal(5, frame.Length)

    [<Fact>]
    let ``A terminal too small to show the header`` () =
        let _, frame = TreeBrowser.frame 1 deep
        Assert.Contains(frame |> List.map text, fun line -> line.StartsWith "> ")

module ``Interactive tree markers remain readable without Unicode`` =
    [<Fact>]
    let ``Console without Unicode support`` () =
        let state = session true ValueNone |> press [ Key.ExpandAll ]
        let ascii = TreeBrowser.frame 100 state |> snd |> Display.mapTexts Display.toAscii |> List.map text
        Assert.All(ascii |> List.collect (fun line -> Seq.toList line), fun c -> Assert.True(int c < 128))

    [<Fact>]
    let ``Cursor row without color`` () =
        Assert.StartsWith("> ", cursorRow (session false ValueNone))
