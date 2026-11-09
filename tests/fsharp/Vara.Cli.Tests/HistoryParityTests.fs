/// Runs the history commands of the C# and F# builds, as processes, on copies of one target that the F# backup wrote, and
/// compares what a script can see: exit codes, restored files byte for byte, and the rows of listings after the borders
/// and the line wrapping (which depend on the console width) are taken out.
///
/// Not covered here, on purpose: the interactive flows (a child process cannot be driven through a prompt, so they are
/// tested in process with scripted prompts), and the directory restore cancelled by Ctrl+C (a graceful Ctrl+C cannot be
/// delivered to a child process, so cancellation rests on the engine tests and the in-process command test).
/// A restored file's modification time is not compared: the F# restore sets the time the file had at the source, which
/// the C# restore does not.
module Vara.Cli.Tests.HistoryParityTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.CommandParityTests
open Vara.Cli.Tests.HistoryCommandTests

let private copyDirectory (source: string) (destination: string) : unit =
    Directory.CreateDirectory destination |> ignore

    for directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories) do
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory))) |> ignore

    for file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
        File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)))

/// Two copies of the target the F# backup wrote, each with a configuration of its own.
type private Pair =
    { Setup: Scratch
      CsConfig: string
      FsConfig: string }

let private withPair (body: Pair -> unit) : unit =
    scratch (fun setup ->
        write setup "a.txt" "first content\nline two\nline three"
        write setup "dir\\kept.txt" "kept"
        write setup "dir\\gone.txt" "gone"
        backupOnce setup
        changeFile setup "a.txt" "second content\nline two\nline 3"
        File.Delete(sourceFile setup "dir\\gone.txt")
        changeFile setup "dir\\later.txt" "later"
        backupOnce setup
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

        let configFor (name: string) : string =
            let target = Path.Combine(setup.Root, $"target-{name}")
            copyDirectory setup.Target target
            let config = Path.Combine(setup.Root, $"profiles-{name}.yml")

            File.WriteAllText(
                config,
                $"profiles:\n  - name: docs\n    target: {target}\n    sources:\n      - path: {setup.Source}\n"
            )

            config

        body
            { Setup = setup
              CsConfig = configFor "cs"
              FsConfig = configFor "fs" })

let private outDir (pair: Pair) (name: string) : string = Path.Combine(pair.Setup.Root, $"out-{name}")

/// One command, once per build, each against its own copy of the target.
let private both (pair: Pair) (parts: string list) : Run * Run =
    let withConfig (config: string) = parts @ [ "--profile"; "docs"; "--config"; config ]
    start csharpDll (withConfig pair.CsConfig), start fsharpDll (withConfig pair.FsConfig)

/// The same invocation, where an argument may differ per build (an output directory): given the build's name.
let private bothWith (pair: Pair) (parts: string -> string list) : Run * Run =
    let withConfig (config: string) (name: string) = parts name @ [ "--profile"; "docs"; "--config"; config ]
    start csharpDll (withConfig pair.CsConfig "cs"), start fsharpDll (withConfig pair.FsConfig "fs")

let private borders = "│┌┐└┘├┤┬┴┼─╭╮╰╯|+`-"

/// A line of a table or a tree with its borders and guides taken out and its spaces collapsed.
let private cells (line: string) : string =
    let blanked = String(line |> Seq.map (fun c -> if borders.Contains c then ' ' elif c = ':' then '.' else c) |> Array.ofSeq)
    String.Join(" ", blanked.Split([| ' '; '\t'; '\r' |], StringSplitOptions.RemoveEmptyEntries))

/// The rows of the output that `project` picks out. A console is 80 columns wide when its output is redirected, so the
/// C# build folds and shortens the cells of a wide table where the F# build prints them whole: only what cannot be
/// shortened is compared, row by row.
let private rowsOf (project: string -> string option) (text: string) : string list =
    text.Split('\n') |> Seq.map cells |> Seq.choose project |> List.ofSeq

let private firstMatch (pattern: string) (line: string) : string option =
    let found = Regex.Match(line, pattern)

    if found.Success then
        Some(String.Join(" ", found.Groups |> Seq.skip 1 |> Seq.map (fun group -> group.Value)))
    else
        None

/// A row of `snapshots` or `history`: its id and its status or change kind, the first and a short cell.
let private idAndKind =
    firstMatch @"^(\d+) .*?\b(Complete|Failed|Cancelled|Running|Added|Changed|Moved|Deleted|Linked)\b"

/// A row of `browse`: the name, its kind and its status.
let private entry = firstMatch @"^(\S+) (File|Directory) (Live|Deleted|Moved)"

/// A row of `deleted`: its size, the last and a short cell.
let private sizeOnly = firstMatch @"(\d+ [KMGT]?B)$"

/// A line of a snapshot report: the whole line.
let private wholeLine (line: string) : string option = if line.Length = 0 then None else Some line

let private sameRows (project: string -> string option) (cs: Run, fs: Run) : unit =
    Assert.Equal(cs.ExitCode, fs.ExitCode)
    let csRows, fsRows = rowsOf project cs.Out, rowsOf project fs.Out
    Assert.NotEmpty fsRows
    Assert.True((csRows = fsRows), "C#:
" + String.Join("
", csRows) + "
F#:
" + String.Join("
", fsRows))

/// A report whose lines may be folded at the console's width: the same characters in the same order, with the line
/// breaks and the spaces taken out.
let private sameStream (cs: Run, fs: Run) : unit =
    Assert.Equal(cs.ExitCode, fs.ExitCode)
    let stream (run: Run) = String.Join("", rowsOf wholeLine run.Out).Replace(" ", "")
    Assert.NotEmpty(stream fs)
    Assert.Equal(stream cs, stream fs)

/// What a restore said, as its characters counted: the same sentence apart from how it is wrapped. Without the
/// progress lines the C# restore prints for every restore (the F# restore shows none for a small one), and with the
/// name of each build's output directory made the same.
let private unified (text: string) : string =
    text.Split('\n')
    |> Seq.filter (fun line -> not (line.Contains "ETA "))
    |> Seq.map (fun line -> line.Replace("out-cs", "out-X").Replace("out-fs", "out-X"))
    |> Seq.collect (fun line -> line |> Seq.filter (fun c -> not (Char.IsWhiteSpace c)) |> Seq.map string)
    |> Seq.sort
    |> String.concat ""

let private sameFiles (cs: string) (fs: string) : unit =
    let tree (root: string) =
        if Directory.Exists root then
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            |> Seq.map (fun file -> Path.GetRelativePath(root, file), Convert.ToHexString(File.ReadAllBytes file))
            |> Seq.sort
            |> List.ofSeq
        else
            []

    Assert.Equal<(string * string) list>(tree cs, tree fs)

module ``History command parity`` =
    [<Fact>]
    let ``the built commands exist`` () =
        Assert.True(File.Exists csharpDll, $"build the C# CLI first: {csharpDll}")
        Assert.True(File.Exists fsharpDll)

    [<Fact>]
    let ``snapshots lists the same snapshots`` () =
        withPair (fun pair ->
            let cs = start csharpDll [ "snapshots"; "docs"; "--config"; pair.CsConfig ]
            let fs = start fsharpDll [ "snapshots"; "docs"; "--config"; pair.FsConfig ]
            sameRows idAndKind (cs, fs)
            Assert.Equal(0, fs.ExitCode))

    [<Fact>]
    let ``history lists the same versions`` () =
        withPair (fun pair ->
            let result = both pair [ "history"; sourceFile pair.Setup "a.txt" ]
            sameRows idAndKind result
            Assert.Equal(0, (snd result).ExitCode))

    [<Fact>]
    let ``browse lists the same entries, with and without deleted ones`` () =
        withPair (fun pair ->
            let directory = Path.GetDirectoryName(keyOf pair.Setup "dir\\kept.txt") |> nonNull
            sameRows entry (both pair [ "browse"; directory ])
            sameRows entry (both pair [ "browse"; directory; "--deleted" ])
            Assert.Equal(0, (snd (both pair [ "browse"; directory ])).ExitCode))

    [<Fact>]
    let ``deleted reports the same files`` () =
        withPair (fun pair ->
            let result = both pair [ "deleted" ]
            sameRows sizeOnly result
            Assert.Contains("gone.txt", (snd result).Out))

    [<Fact>]
    let ``snapshot reports the same changes`` () =
        withPair (fun pair ->
            sameStream (both pair [ "snapshot"; "1"; "--files" ])
            sameStream (both pair [ "snapshot"; "2"; "--depth"; "1" ])
            // An unknown snapshot is an error in both, with nothing on standard output.
            let cs, fs = both pair [ "snapshot"; "99" ]
            Assert.Equal(cs.ExitCode, fs.ExitCode)
            Assert.Equal(1, fs.ExitCode))

    [<Fact>]
    let ``show streams the same content`` () =
        withPair (fun pair ->
            let first = string (versionIds pair.Setup "a.txt").Head
            let cs, fs = both pair [ "show"; sourceFile pair.Setup "a.txt"; "--version"; first ]
            Assert.Equal(cs.ExitCode, fs.ExitCode)
            Assert.Equal(cs.Out, fs.Out)
            Assert.Contains("first content", fs.Out))

    [<Fact>]
    let ``diff shows the same lines`` () =
        withPair (fun pair ->
            let ids = versionIds pair.Setup "a.txt"

            let cs, fs =
                both pair [ "diff"; sourceFile pair.Setup "a.txt"; "--left-version"; string ids[0]; "--right-version"; string ids[1] ]

            Assert.Equal(cs.ExitCode, fs.ExitCode)
            Assert.Equal(cs.Out.Replace("\r\n", "\n"), fs.Out.Replace("\r\n", "\n"))
            Assert.Contains("+ line 3", fs.Out))

    [<Fact>]
    let ``restore of a file writes the same bytes and gives the same exit code`` () =
        withPair (fun pair ->
            let first = string (versionIds pair.Setup "a.txt").Head

            let cs, fs =
                bothWith pair (fun name ->
                    [ "restore"; sourceFile pair.Setup "a.txt"; "--version"; first; "--out"; Path.Combine(outDir pair name, "a.txt") ])

            Assert.Equal(cs.ExitCode, fs.ExitCode)
            Assert.Equal(0, fs.ExitCode)
            sameFiles (outDir pair "cs") (outDir pair "fs")
            // The same sentence, apart from the destination each build was given.
            Assert.Equal(unified cs.Out, unified fs.Out))

    [<Fact>]
    let ``restore of a directory writes the same files and gives the same exit code`` () =
        withPair (fun pair ->
            let directory = Path.GetDirectoryName(keyOf pair.Setup "dir\\kept.txt") |> nonNull
            let at = betweenFirstTwo pair.Setup

            let cs, fs =
                bothWith pair (fun name -> [ "restore"; directory; "--recursive"; "--at"; at; "--out"; outDir pair name; "--force" ])

            Assert.Equal(cs.ExitCode, fs.ExitCode)
            Assert.Equal(0, fs.ExitCode)
            sameFiles (outDir pair "cs") (outDir pair "fs")
            Assert.True(File.Exists(Path.Combine(outDir pair "fs", "gone.txt"))))

    [<Fact>]
    let ``refusals give the same exit code`` () =
        withPair (fun pair ->
            let first = string (versionIds pair.Setup "a.txt").Head

            let cases =
                [ [ "history"; "never.txt" ]
                  [ "show"; "never.txt"; "--version"; "1" ]
                  [ "snapshots"; "nonexistent-profile" ]
                  [ "browse"; "never\\tracked" ]
                  [ "restore"; sourceFile pair.Setup "a.txt"; "--version"; first; "--at"; "2020-01-01"; "--out"; "x" ]
                  [ "browse"; "--at"; "yesterday" ] ]

            for parts in cases do
                let cs, fs = both pair parts
                Assert.Equal(cs.ExitCode, fs.ExitCode)
                Assert.NotEqual(0, fs.ExitCode)

            // A restore into the live mirror is refused by both, and writes nothing.
            let mirror (name: string) = Path.Combine(pair.Setup.Root, $"target-{name}", "restored.txt")

            let cs, fs =
                bothWith pair (fun name -> [ "restore"; sourceFile pair.Setup "a.txt"; "--version"; first; "--out"; mirror name ])

            Assert.Equal(cs.ExitCode, fs.ExitCode)
            Assert.Equal(1, fs.ExitCode)
            Assert.False(File.Exists(mirror "cs") || File.Exists(mirror "fs")))
