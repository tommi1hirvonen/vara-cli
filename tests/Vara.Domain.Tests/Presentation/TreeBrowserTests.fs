/// Tests of the interactive change tree's state machine and frames. Each module is named after a requirement of the
/// interactive-change-tree and interactive-tree-files changes (snapshot-history and cli-presentation), and each test
/// after one of its scenarios.
module Vara.Domain.Tests.TreeBrowserTests

open Xunit
open Vara.Domain

let private entry (path: string) (kind: ChangeKind) : ChangeEntry =
    { ChangedPath = path
      ChangedKind = kind
      MovedFrom = ValueNone }

let private movedEntry (path: string) (previous: string) : ChangeEntry =
    { ChangedPath = path
      ChangedKind = ChangeKind.Moved
      MovedFrom = ValueSome previous }

let private changes =
    [ entry "C\\a\\one.txt" ChangeKind.Added
      entry "C\\a\\b\\two.txt" ChangeKind.Changed
      entry "C\\a\\b\\c\\three.txt" ChangeKind.Deleted
      movedEntry "C\\a\\moved.txt" "C\\z\\old.txt" ]

let private header: Display = [ Line.Text(Tone.Plain, "Snapshot #1") ]

/// A session over the changes. Without files, the browser is given the directories only, as if no file entries existed.
let private sessionOf (all: ChangeEntry list) (files: bool) (depth: int voption) : TreeState =
    TreeBrowser.create header "C" Set.empty depth (History.rollupEntries "C" false all) (if files then Array.ofList all else Array.empty)

let private session (files: bool) (depth: int voption) : TreeState = sessionOf changes files depth

let private press (keys: Key list) (state: TreeState) : TreeState =
    keys |> List.fold (fun state key -> TreeBrowser.step key state) state

let private text (line: Line) : string =
    match line with
    | Line.Spans spans -> spans |> List.map _.Text |> String.concat ""
    | Line.Text(_, value) -> value
    | _ -> ""

let private frameLines (height: int) (state: TreeState) : string list =
    TreeBrowser.frame 1000 height state |> snd |> List.map text

/// The tree rows only: no header, no detail line, no status line.
let private treeRows (state: TreeState) : string list =
    let lines = frameLines 100 state
    lines |> List.skip 1 |> List.take (lines.Length - 3)

let private detailLine (state: TreeState) : string =
    let lines = frameLines 100 state
    lines[lines.Length - 2]

let private statusLine (state: TreeState) : string = frameLines 100 state |> List.last

let private cursorRow (state: TreeState) : string =
    treeRows state |> List.find (fun row -> row.StartsWith "> ")

let private countsOf (row: string) : string = row.Substring(row.IndexOf '(')

/// The rows without the cursor gutter, so that two frames can be compared whatever row the cursor is on.
let private plainRows (state: TreeState) : string list = treeRows state |> List.map _.Substring(2)

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
        Assert.Equal<string list>(plainRows before, plainRows state)

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
        Assert.Contains(shown, fun row -> row.Contains "+  one.txt")
        Assert.Contains(shown, fun row -> row.Contains "-> moved.txt")

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

module ``Interactive file rows with change symbols`` =
    let private kinds =
        [ entry "C\\d\\added.txt" ChangeKind.Added
          entry "C\\d\\changed.txt" ChangeKind.Changed
          movedEntry "C\\d\\moved.txt" "C\\e\\moved.txt"
          entry "C\\d\\deleted.txt" ChangeKind.Deleted
          entry "C\\d\\link" ChangeKind.Linked ]

    let private expandedKinds = sessionOf kinds true (ValueSome 5) |> press [ Key.ExpandAll ]

    [<Fact>]
    let ``Files under the deepest directory`` () =
        // `d` has no subdirectory: it is the deepest directory, and still lists its files.
        let shown = treeRows expandedKinds
        Assert.Equal(1 + 1 + 5, shown.Length)
        Assert.Contains(shown, fun row -> row.Contains "[-] d")
        Assert.Contains(shown, fun row -> row.Contains "+  added.txt")

    [<Fact>]
    let ``Symbols for each change kind`` () =
        let symbolOf (name: string) =
            frameLines 100 expandedKinds |> List.find (fun line -> line.EndsWith name)

        Assert.Contains("+  added.txt", symbolOf "added.txt")
        Assert.Contains("~  changed.txt", symbolOf "changed.txt")
        Assert.Contains("-> moved.txt", symbolOf "moved.txt")
        Assert.Contains("-  deleted.txt", symbolOf "deleted.txt")
        Assert.Contains("@  link", symbolOf "link")

    [<Fact>]
    let ``Each symbol is in the color of its kind`` () =
        let tones =
            TreeBrowser.frame 1000 100 expandedKinds
            |> snd
            |> List.choose (function
                | Line.Spans [ _; symbol; name ] -> Some(name.Text.Trim(), symbol.Tone)
                | _ -> None)
            |> Map.ofList

        Assert.Equal(Tone.Added, tones["added.txt"])
        Assert.Equal(Tone.Changed, tones["changed.txt"])
        Assert.Equal(Tone.Moved, tones["moved.txt"])
        Assert.Equal(Tone.Deleted, tones["deleted.txt"])
        Assert.Equal(Tone.Linked, tones["link"])

    [<Fact>]
    let ``Files are ordered after directories`` () =
        let mixed =
            [ entry "C\\d\\z.txt" ChangeKind.Added
              entry "C\\d\\sub\\inner.txt" ChangeKind.Added
              entry "C\\d\\B.txt" ChangeKind.Added
              entry "C\\d\\a.txt" ChangeKind.Added ]

        let shown = treeRows (sessionOf mixed true (ValueSome 5) |> press [ Key.ExpandAll ])
        let order = shown |> List.map (fun row -> row.TrimStart('>', ' ', '│', '├', '└', '─'))
        let at (suffix: string) = order |> List.findIndex (fun row -> row.EndsWith suffix)
        // The subdirectory first, then the files by name without regard to case.
        Assert.True(at "sub (+1 ~0 ->0 -0 link 0)" < at "a.txt")
        Assert.True(at "a.txt" < at "B.txt")
        Assert.True(at "B.txt" < at "z.txt")

    [<Fact>]
    let ``Detail line for a moved file`` () =
        let state = expandedKinds |> press [ Key.End ]
        // End is the last row; walk to the moved row by name instead.
        let onMoved =
            expandedKinds
            |> fun s -> (treeRows s |> List.findIndex (fun row -> row.EndsWith "moved.txt")) |> fun i -> press (List.replicate i Key.Down) s

        Assert.Equal("C\\d\\moved.txt  (moved from C\\e\\moved.txt)", detailLine onMoved)
        Assert.NotEmpty(treeRows state)

    [<Fact>]
    let ``Detail line for a directory`` () =
        let onDirectory = expandedKinds |> press [ Key.Down ]
        Assert.Contains("[-] d", cursorRow onDirectory)
        Assert.Equal("C\\d", detailLine onDirectory)
        Assert.Equal("C", detailLine (sessionOf kinds true ValueNone))

        // Scoped to the whole mirror, the root has no path of its own.
        let wholeMirror =
            TreeBrowser.create header "" Set.empty ValueNone (History.rollupEntries "" false kinds) (Array.ofList kinds)

        Assert.Equal("(mirror root)", detailLine wholeMirror)

module ``A moved file absorbs its source's deleted row`` =
    let private snapshot =
        [ movedEntry "C\\new\\m.txt" "C\\old\\m.txt"
          entry "C\\old\\m.txt" ChangeKind.Deleted
          entry "C\\old\\gone.txt" ChangeKind.Deleted
          entry "C\\old\\kept.txt" ChangeKind.Added ]

    let private expanded = sessionOf snapshot true (ValueSome 5) |> press [ Key.ExpandAll ]

    [<Fact>]
    let ``A move reads as one row`` () =
        let shown = treeRows expanded
        Assert.Contains(shown, fun row -> row.EndsWith "-> m.txt")
        Assert.Single(shown |> List.filter (fun row -> row.Contains "m.txt")) |> ignore

    [<Fact>]
    let ``Counts still include the absorbed deletion`` () =
        let shown = treeRows expanded
        let oldRow = shown |> List.find (fun row -> row.Contains " old (")
        Assert.Contains("(+1 ~0 ->0 -2 link 0)", oldRow)

        // The same counts as the static report.
        let staticTree =
            HistoryView.treeLines "C" Set.empty ValueNone (History.rollupEntries "C" false snapshot)
            |> List.map text

        let staticOld = staticTree |> List.find (fun row -> row.Contains " old (")
        Assert.Equal<string>(countsOf staticOld, countsOf oldRow)

    [<Fact>]
    let ``A plain deletion is listed`` () =
        Assert.Contains(treeRows expanded, fun row -> row.EndsWith "-  gone.txt")

    [<Fact>]
    let ``Directory detail explains absorbed deletions`` () =
        let onOld = expanded |> press [ Key.Down; Key.Down; Key.Down ]
        Assert.Contains(" old (", cursorRow onOld)
        Assert.Equal("C\\old  (1 deleted entry not listed: moved)", detailLine onOld)

    [<Fact>]
    let ``The match ignores case`` () =
        let upper =
            [ movedEntry "C\\new\\m.txt" "C\\OLD\\M.TXT"
              entry "C\\old\\m.txt" ChangeKind.Deleted ]

        let shown = treeRows (sessionOf upper true (ValueSome 5) |> press [ Key.ExpandAll ])
        Assert.DoesNotContain(shown, fun row -> row.EndsWith "- m.txt")

module ``Toggling deleted and file rows`` =
    let private snapshot =
        [ entry "C\\d\\added.txt" ChangeKind.Added
          entry "C\\d\\gone.txt" ChangeKind.Deleted
          entry "C\\d\\also.txt" ChangeKind.Deleted
          entry "C\\only\\kept.txt" ChangeKind.Added ]

    let private expanded = sessionOf snapshot true (ValueSome 5) |> press [ Key.ExpandAll ]

    let private rowsContaining (needle: string) (state: TreeState) : int =
        treeRows state |> List.filter (fun row -> row.Contains needle) |> List.length

    [<Fact>]
    let ``Deleted rows are shown by default`` () =
        Assert.Equal(1, rowsContaining "-  gone.txt" expanded)
        Assert.Contains("d deleted: shown", statusLine expanded)
        Assert.Contains("f files: shown", statusLine expanded)

    [<Fact>]
    let ``Hiding deleted rows`` () =
        let hidden = expanded |> press [ Key.ToggleDeleted ]
        Assert.Equal(0, rowsContaining "-  gone.txt" hidden)
        Assert.Equal(1, rowsContaining "+  added.txt" hidden)
        Assert.Contains("d deleted: hidden (2)", statusLine hidden)

        // No directory count changes.
        let counts (state: TreeState) =
            treeRows state |> List.filter (fun row -> row.Contains "(+") |> List.map countsOf

        Assert.Equal<string list>(counts expanded, counts hidden)

    [<Fact>]
    let ``Showing deleted rows again`` () =
        let state = expanded |> press [ Key.ToggleDeleted; Key.ToggleDeleted ]
        Assert.Equal(1, rowsContaining "-  gone.txt" state)
        Assert.Contains("d deleted: shown", statusLine state)

    [<Fact>]
    let ``Hiding all file rows`` () =
        let hidden = expanded |> press [ Key.ToggleFiles ]
        Assert.Equal(0, rowsContaining ".txt" hidden)
        Assert.Contains("f files: hidden (4)", statusLine hidden)
        Assert.Equal(3, (treeRows hidden).Length)

    [<Fact>]
    let ``Directories with nothing visible to expand`` () =
        let hidden = expanded |> press [ Key.ToggleFiles ]
        let onLeaf = hidden |> press [ Key.Down ]
        Assert.Contains(" d (", cursorRow onLeaf)
        Assert.DoesNotContain("[", cursorRow onLeaf)
        Assert.Equal<string list>(plainRows onLeaf, plainRows (onLeaf |> press [ Key.Right ]))

    [<Fact>]
    let ``A directory whose only files are deleted loses its marker when they are hidden`` () =
        let onlyDeleted = sessionOf [ entry "C\\x\\gone.txt" ChangeKind.Deleted; entry "C\\y\\kept.txt" ChangeKind.Added ] true (ValueSome 5)
        let hidden = onlyDeleted |> press [ Key.ExpandAll; Key.ToggleDeleted ]
        let row = treeRows hidden |> List.find (fun row -> row.Contains " x (")
        Assert.DoesNotContain("[", row)

    [<Fact>]
    let ``Cursor on a row that becomes hidden`` () =
        let onDeleted =
            expanded
            |> fun s -> (treeRows s |> List.findIndex (fun row -> row.EndsWith "-  gone.txt")) |> fun i -> press (List.replicate i Key.Down) s

        Assert.Contains("gone.txt", cursorRow onDeleted)
        let hidden = onDeleted |> press [ Key.ToggleDeleted ]
        Assert.Contains(" d (", cursorRow hidden)

    [<Fact>]
    let ``Expanding everything with file rows hidden`` () =
        let state = sessionOf snapshot true (ValueSome 0) |> press [ Key.ToggleFiles; Key.ExpandAll ]
        Assert.Equal(3, (treeRows state).Length)
        Assert.Equal(0, rowsContaining ".txt" state)

    [<Fact>]
    let ``Both toggles off`` () =
        let state = expanded |> press [ Key.ToggleDeleted; Key.ToggleFiles ]
        Assert.Contains("d deleted: hidden (2)", statusLine state)
        Assert.Contains("f files: hidden (4)", statusLine state)

    [<Fact>]
    let ``A narrow terminal drops the key hints and cuts the status line`` () =
        let status (width: int) =
            TreeBrowser.frame width 100 expanded |> snd |> List.map text |> List.last

        Assert.Contains("Up/Down move", status 1000)
        let narrow = status 60
        Assert.DoesNotContain("Up/Down", narrow)
        Assert.Contains("d deleted: shown", narrow)
        Assert.True(narrow.Length <= 59)
        let tiny = status 20
        Assert.True(tiny.Length <= 19)
        Assert.EndsWith("...", tiny)

    [<Fact>]
    let ``A narrow terminal keeps the end of a long path on the detail line`` () =
        let long =
            [ entry "C\\a-long-directory-name\\another-long-directory-name\\deepest\\file.txt" ChangeKind.Added ]

        let state = sessionOf long true (ValueSome 9) |> press [ Key.ExpandAll; Key.End ]
        let detail = TreeBrowser.frame 30 100 state |> snd |> List.map text |> fun lines -> lines[lines.Length - 2]
        Assert.True(detail.Length <= 29)
        Assert.StartsWith("...", detail)
        Assert.EndsWith("deepest\\file.txt", detail)

    [<Fact>]
    let ``Toggles leave the expansion and counts alone`` () =
        let state = sessionOf snapshot true (ValueSome 1)
        let toggled = state |> press [ Key.ToggleDeleted; Key.ToggleFiles; Key.ToggleFiles; Key.ToggleDeleted ]
        Assert.Equal<Set<int>>(state.Expanded, toggled.Expanded)
        Assert.Equal<string list>(plainRows state, plainRows toggled)

module ``Interactive tree scrolls within the terminal`` =
    let private deep = session false ValueNone |> press [ Key.ExpandAll ]

    [<Fact>]
    let ``Cursor moves past the last visible row`` () =
        // A header line, two tree rows, the detail line and the status line: five lines in all.
        let state = deep |> press [ Key.Down; Key.Down ]
        let fitted, frame = TreeBrowser.frame 1000 5 state
        let lines = frame |> List.map text
        Assert.Equal("Snapshot #1", lines.Head)
        Assert.Equal(5, lines.Length)
        Assert.StartsWith("> ", lines[2])
        Assert.Equal(1, fitted.Offset)

    [<Fact>]
    let ``Indicating hidden rows`` () =
        let state = deep |> press [ Key.Down ]
        let status = frameLines 5 state |> List.last
        Assert.Contains("more below", status)
        let scrolled = deep |> press [ Key.Down; Key.Down; Key.Down ]
        Assert.Contains("more above", frameLines 5 scrolled |> List.last)

    [<Fact>]
    let ``Resizing the terminal`` () =
        let fitted, _ = TreeBrowser.frame 1000 100 (deep |> press [ Key.End ])
        let _, small = TreeBrowser.frame 1000 4 fitted
        Assert.Contains(small |> List.map text, fun line -> line.StartsWith "> ")

    [<Fact>]
    let ``Collapsing near the end of the list`` () =
        let atEnd, _ = TreeBrowser.frame 1000 6 (deep |> press [ Key.End ])
        let collapsed = atEnd |> press [ Key.Up; Key.Left ]
        let _, frame = TreeBrowser.frame 1000 6 collapsed
        // The collapse shortened the list to three rows; the window shows all of them, not a blank tail.
        Assert.Equal(6, frame.Length)

    [<Fact>]
    let ``A terminal too small to show the header`` () =
        let _, frame = TreeBrowser.frame 1000 1 deep
        Assert.Contains(frame |> List.map text, fun line -> line.StartsWith "> ")

module ``Interactive tree markers remain readable without Unicode`` =
    [<Fact>]
    let ``Console without Unicode support`` () =
        let state = session true ValueNone |> press [ Key.ExpandAll ]
        let ascii = TreeBrowser.frame 1000 100 state |> snd |> Display.mapTexts Display.toAscii |> List.map text
        Assert.All(ascii |> List.collect (fun line -> Seq.toList line), fun c -> Assert.True(int c < 128))

    [<Fact>]
    let ``Cursor row without color`` () =
        Assert.StartsWith("> ", cursorRow (session false ValueNone))

module ``Interactive file symbols remain readable without Unicode or color`` =
    let private kinds =
        [ entry "C\\d\\a.txt" ChangeKind.Added
          entry "C\\d\\b.txt" ChangeKind.Changed
          movedEntry "C\\d\\c.txt" "C\\e\\c.txt"
          entry "C\\d\\d.txt" ChangeKind.Deleted
          entry "C\\d\\e" ChangeKind.Linked ]

    [<Fact>]
    let ``Console without Unicode support`` () =
        let state = sessionOf kinds true (ValueSome 5) |> press [ Key.ExpandAll ]
        let ascii = TreeBrowser.frame 1000 100 state |> snd |> Display.mapTexts Display.toAscii |> List.map text
        Assert.All(ascii |> List.collect (fun line -> Seq.toList line), fun c -> Assert.True(int c < 128))

        for symbol, name in [ "+ ", "a.txt"; "~ ", "b.txt"; "->", "c.txt"; "- ", "d.txt"; "@ ", "e" ] do
            Assert.Contains(ascii, fun line -> line.EndsWith(symbol + " " + name))

    [<Fact>]
    let ``No color`` () =
        // The symbol is part of the row's text, so a row's kind reads without its tone.
        let state = sessionOf kinds true (ValueSome 5) |> press [ Key.ExpandAll; Key.ToggleDeleted ]
        let rows = treeRows state
        Assert.Contains(rows, fun row -> row.EndsWith "~  b.txt")
        Assert.Contains("d deleted: hidden", statusLine state)
