/// The cases of the history corpus: the read-only history commands and `restore`, run on a target that the F# backup
/// wrote, observed as exit codes, restored files byte for byte, and the rows of listings after the borders and the line
/// wrapping (which depend on the console width) are taken out.
///
/// Not covered here, on purpose: the interactive flows (a child process cannot be driven through a prompt, so they are
/// tested in process with scripted prompts), and the directory restore cancelled by Ctrl+C (a graceful Ctrl+C cannot be
/// delivered to a child process, so cancellation rests on the engine tests and the in-process command test).
/// A restored file's modification time is not observed: the F# restore sets the time the file had at the source, which
/// the C# restore does not.
module Vara.Cli.Tests.HistoryCorpus

open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.Data.Sqlite
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests
open Vara.Cli.Tests.ProcessCorpus
open Vara.Testing.Anonymize

/// A target with two snapshots: a file changed, one deleted and one added between them.
let private withTarget (body: Scratch -> string) : string =
    observing (fun setup ->
        write setup "a.txt" "first content\nline two\nline three"
        write setup "dir\\kept.txt" "kept"
        write setup "dir\\gone.txt" "gone"
        backupOnce setup
        changeFile setup "a.txt" "second content\nline two\nline 3"
        File.Delete(sourceFile setup "dir\\gone.txt")
        changeFile setup "dir\\later.txt" "later"
        backupOnce setup
        SqliteConnection.ClearAllPools()
        body setup)

let private outDir (setup: Scratch) : string = Path.Combine(setup.Root, "out")

/// One command for the profile of the scratch directory.
let private command (dll: string) (setup: Scratch) (parts: string list) : Run =
    start dll (parts @ [ "--profile"; "docs"; "--config"; setup.Config ])

let private borders = "│┌┐└┘├┤┬┴┼─╭╮╰╯|+`-"

/// A line of a table or a tree with its borders and guides taken out and its spaces collapsed.
let private cells (line: string) : string =
    let blanked = String(line |> Seq.map (fun c -> if borders.Contains c then ' ' elif c = ':' then '.' else c) |> Array.ofSeq)
    String.Join(" ", blanked.Split([| ' '; '\t'; '\r' |], StringSplitOptions.RemoveEmptyEntries))

/// The rows of the output that `project` picks out. A console is 80 columns wide when its output is redirected, so the
/// C# build folds and shortens the cells of a wide table where the F# build prints them whole: only what cannot be
/// shortened is observed, row by row.
let private rowsOf (project: string -> string option) (output: string) : string list =
    output.Split('\n') |> Seq.map cells |> Seq.choose project |> List.ofSeq

let private firstMatch (pattern: string) (line: string) : string option =
    let found = Regex.Match(line, pattern)

    if found.Success then
        Some(String.Join(" ", found.Groups |> Seq.skip 1 |> Seq.map _.Value))
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

let private rows (project: string -> string option) (label: string) (run: Run) : string list =
    line (label + " exit") (string run.ExitCode) :: lines (label + " rows") (rowsOf project run.Out)

/// The directories of the scratch directory's own path, which a tree shows as its ancestors and which differ between
/// machines and runs.
let private ancestors (setup: Scratch) : string list =
    (longPath setup.Source).Split([| '\\'; ':' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray |> List.filter (fun part -> part <> "source")

/// A report whose lines may be folded at the console's width: the characters in order, with the line breaks and the
/// spaces taken out. The tree's rows for the scratch directory's ancestors, the times and the scratch directory itself
/// are taken out, since they differ between machines and runs. A row is `name(counts)` once flattened, which a fold of
/// the line does not change.
let private stream (setup: Scratch) (run: Run) : string =
    let flat (value: string) = String.Join("", rowsOf wholeLine value).Replace(" ", "")

    let withoutAncestors (text: string) : string =
        // The longest name first and only at the start of a name, so that the last letter of one directory's name is never
        // taken for a directory of its own (a drive `C` against a name that ends in `c`).
        ancestors setup
        |> List.sortByDescending _.Length
        |> List.fold (fun (text: string) (part: string) -> Regex.Replace(text, @"(?<![A-Za-z0-9])" + Regex.Escape(flat part) + @"\([^)]*\)", "")) text

    anonymizeFlat flat setup.Root (withoutAncestors (flat (withoutTimes run.Out)))

/// The depth that shows the tree down to the source directory and hides what is below it.
let private depthToSource (setup: Scratch) : string =
    string ((longPath setup.Source).Split([| '\\' |], StringSplitOptions.RemoveEmptyEntries).Length)

let private streamLines (label: string) (setup: Scratch) (run: Run) : string list =
    [ line (label + " exit") (string run.ExitCode); line (label + " stream") (stream setup run) ]

/// What a restore said, as its characters counted: the same sentence apart from how it is wrapped. Without the
/// progress lines the C# restore prints for every restore (the F# restore shows none for a small one), and with the
/// scratch directory taken out wherever the wrapping left it whole.
let private unified (setup: Scratch) (output: string) : string =
    let flat =
        output.Split('\n')
        |> Seq.filter (fun line -> not (line.Contains "ETA "))
        |> Seq.collect (fun line -> line |> Seq.filter (fun c -> not (Char.IsWhiteSpace c)))
        |> Array.ofSeq
        |> String

    let despaced (value: string) = String(value |> Seq.filter (fun c -> not (Char.IsWhiteSpace c)) |> Array.ofSeq)

    anonymizeFlat despaced setup.Root flat
    |> Seq.sort
    |> Seq.map string
    |> String.concat ""

/// The files under a directory with their contents, so that restored bytes are compared exactly.
let private tree (root: string) : string list =
    if Directory.Exists root then
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        |> Seq.map (fun file -> $"{Path.GetRelativePath(root, file)} {Convert.ToHexString(File.ReadAllBytes file)}")
        |> Seq.sort
        |> List.ofSeq
    else
        []

let private snapshots (dll: string) : string =
    withTarget (fun setup -> text (rows idAndKind "snapshots" (start dll [ "snapshots"; "docs"; "--config"; setup.Config ])))

let private history (dll: string) : string =
    withTarget (fun setup -> text (rows idAndKind "history" (command dll setup [ "history"; sourceFile setup "a.txt" ])))

let private browse (dll: string) : string =
    withTarget (fun setup ->
        let directory = Path.GetDirectoryName(keyOf setup "dir\\kept.txt") |> nonNull

        text
            [ yield! rows entry "browse" (command dll setup [ "browse"; directory ])
              yield! rows entry "browse deleted" (command dll setup [ "browse"; directory; "--deleted" ]) ])

let private deleted (dll: string) : string =
    withTarget (fun setup ->
        let run = command dll setup [ "deleted" ]

        text
            [ yield! rows sizeOnly "deleted" run
              line "names gone.txt" (string (run.Out.Contains "gone.txt")) ])

let private snapshot (dll: string) : string =
    withTarget (fun setup ->
        text
            [ yield! streamLines "snapshot 1 files" setup (command dll setup [ "snapshot"; "1"; "--files" ])
              yield! streamLines "snapshot 2 depth" setup (command dll setup [ "snapshot"; "2"; "--depth"; depthToSource setup ])
              // An unknown snapshot is an error, with nothing on standard output.
              line "snapshot 99 exit" (string (command dll setup [ "snapshot"; "99" ]).ExitCode) ])

let private show (dll: string) : string =
    withTarget (fun setup ->
        let first = string (versionIds setup "a.txt").Head
        let run = command dll setup [ "show"; sourceFile setup "a.txt"; "--version"; first ]
        text [ line "exit" (string run.ExitCode); line "content" run.Out ])

let private diff (dll: string) : string =
    withTarget (fun setup ->
        let ids = versionIds setup "a.txt"

        let run =
            command dll setup [ "diff"; sourceFile setup "a.txt"; "--left-version"; string ids[0]; "--right-version"; string ids[1] ]

        text [ line "exit" (string run.ExitCode); line "diff" (run.Out.Replace("\r\n", "\n")) ])

let private restoreFile (dll: string) : string =
    withTarget (fun setup ->
        let first = string (versionIds setup "a.txt").Head

        let run =
            command dll setup [ "restore"; sourceFile setup "a.txt"; "--version"; first; "--out"; Path.Combine(outDir setup, "a.txt") ]

        text
            [ line "exit" (string run.ExitCode)
              yield! lines "files" (tree (outDir setup))
              line "said" (unified setup run.Out) ])

let private restoreDirectory (dll: string) : string =
    withTarget (fun setup ->
        let directory = Path.GetDirectoryName(keyOf setup "dir\\kept.txt") |> nonNull

        let run =
            command dll setup [ "restore"; directory; "--recursive"; "--at"; betweenFirstTwo setup; "--out"; outDir setup; "--force" ]

        text [ line "exit" (string run.ExitCode); yield! lines "files" (tree (outDir setup)) ])

let private refusals (dll: string) : string =
    withTarget (fun setup ->
        let first = string (versionIds setup "a.txt").Head

        let cases =
            [ "history of an untracked file", [ "history"; "never.txt" ]
              "show of an untracked file", [ "show"; "never.txt"; "--version"; "1" ]
              "snapshots of an unknown profile", [ "snapshots"; "nonexistent-profile" ]
              "browse of an untracked directory", [ "browse"; "never\\tracked" ]
              "restore with a version and a time", [ "restore"; sourceFile setup "a.txt"; "--version"; first; "--at"; "2020-01-01"; "--out"; "x" ]
              "browse with an unreadable time", [ "browse"; "--at"; "yesterday" ] ]

        // A restore into the live mirror is refused, and writes nothing.
        let mirror = Path.Combine(setup.Target, "restored.txt")
        let into = command dll setup [ "restore"; sourceFile setup "a.txt"; "--version"; first; "--out"; mirror ]

        text
            [ for name, parts in cases do
                  yield line name (string (command dll setup parts).ExitCode)
              yield line "restore into the mirror exit" (string into.ExitCode)
              yield line "restore into the mirror wrote a file" (string (File.Exists mirror)) ])

let cases: Case list =
    [ { Name = "snapshots"; Observe = snapshots }
      { Name = "history"; Observe = history }
      { Name = "browse"; Observe = browse }
      { Name = "deleted"; Observe = deleted }
      { Name = "snapshot"; Observe = snapshot }
      { Name = "show"; Observe = show }
      { Name = "diff"; Observe = diff }
      { Name = "restore-file"; Observe = restoreFile }
      { Name = "restore-directory"; Observe = restoreDirectory }
      { Name = "refusals"; Observe = refusals } ]
