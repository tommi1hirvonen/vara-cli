/// Tests of `vara restore`, through the command against a real target written by the F# backup: a file and a
/// directory, every guard, every prompt. Each module is named after a requirement of
/// openspec/specs/snapshot-history/spec.md and each test after one of its scenarios.
module Vara.Cli.Tests.RestoreCommandTests

open System
open System.IO
open System.Threading
open Xunit
open Vara.Cli
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests
open Vara.Domain

let private outDir (setup: Scratch) : string = Path.Combine(setup.Root, "out")
let private outFile (setup: Scratch) (name: string) : string = Path.Combine(outDir setup, name)

let private restoreWith (customize: CliEnv -> CliEnv) (setup: Scratch) (parts: string list) : Invocation =
    runEnv customize setup ([ "restore" ] @ parts)

let private restore (setup: Scratch) (parts: string list) : Invocation = restoreWith id setup parts

/// Prompts that answer as scripted, recording every question and every list of choices put to the user.
type private Script(select: int, confirm: bool) =
    member val Questions = ResizeArray<string>()
    member val Choices = ResizeArray<Span list list>()

    member this.Prompts: Prompts =
        { Select =
            fun title choices _ ->
                this.Questions.Add title
                this.Choices.Add choices
                select
          Confirm =
            fun question ->
                this.Questions.Add question
                confirm }

let private text (choice: Span list) : string = choice |> List.map (fun span -> span.Text) |> String.concat ""

/// Two versions of a.txt: the first is "first content", the second "second content, longer".
let private twoVersions (setup: Scratch) : unit =
    write setup "a.txt" "first content"
    backupOnce setup
    changeFile setup "a.txt" "second content, longer"
    backupOnce setup

let private noOutput (result: Invocation) : unit = Assert.DoesNotContain("Restored", result.Output)

module ``Restore a file at a given version or date`` =
    [<Fact>]
    let ``Restoring a previous version`` () =
        scratch (fun setup ->
            twoVersions setup
            let destination = outFile setup "a.txt"
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; destination ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Restored", result.Output)
            Assert.Equal("first content", File.ReadAllText destination)
            // The live mirror is left as it was.
            Assert.Equal("second content, longer", File.ReadAllText(Path.Combine(setup.Target, keyOf setup "a.txt"))))

    [<Fact>]
    let ``a version id restores that version and its modification time`` () =
        scratch (fun setup ->
            write setup "a.txt" "first content"
            File.SetLastWriteTimeUtc(sourceFile setup "a.txt", DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc))
            backupOnce setup
            let destination = outFile setup "a.txt"
            let result = restore setup [ sourceFile setup "a.txt"; "--version"; string (versionIds setup "a.txt").Head; "--out"; destination ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal(DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc), File.GetLastWriteTimeUtc destination))

    [<Fact>]
    let ``Destination does not already exist`` () =
        scratch (fun setup ->
            twoVersions setup
            let destination = outFile setup "new\\deeper\\a.txt"
            // No prompt is expected: the default prompts fail the test if one is asked.
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; destination ]
            Assert.Equal(0, result.ExitCode)
            Assert.True(File.Exists destination))

    [<Fact>]
    let ``Destination exists and an explicit overwrite override is given`` () =
        scratch (fun setup ->
            twoVersions setup
            let destination = outFile setup "a.txt"
            Directory.CreateDirectory(outDir setup) |> ignore
            File.WriteAllText(destination, "old")
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; destination; "--force" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("first content", File.ReadAllText destination))

    [<Fact>]
    let ``Destination exists, running interactively, no explicit override given`` () =
        scratch (fun setup ->
            twoVersions setup
            let destination = outFile setup "a.txt"
            Directory.CreateDirectory(outDir setup) |> ignore
            File.WriteAllText(destination, "old")
            let script = Script(0, false)
            let result = restoreWith (interactive script.Prompts) setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; destination ]
            Assert.Equal(0, result.ExitCode)
            // One question, naming the destination, and the default is not to overwrite.
            Assert.Single script.Questions |> ignore
            Assert.Contains(destination, script.Questions[0])
            Assert.Equal("old", File.ReadAllText destination))

    [<Fact>]
    let ``User confirms the interactive overwrite prompt`` () =
        scratch (fun setup ->
            twoVersions setup
            let destination = outFile setup "a.txt"
            Directory.CreateDirectory(outDir setup) |> ignore
            File.WriteAllText(destination, "old")
            let result = restoreWith (interactive (Script(0, true)).Prompts) setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; destination ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("first content", File.ReadAllText destination))

    [<Fact>]
    let ``User declines the interactive overwrite prompt`` () =
        scratch (fun setup ->
            twoVersions setup
            let destination = outFile setup "a.txt"
            Directory.CreateDirectory(outDir setup) |> ignore
            File.WriteAllText(destination, "old")
            let result = restoreWith (interactive (Script(0, false)).Prompts) setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; destination ]
            // Cancelled, and not an error.
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Restore cancelled", result.Output)
            Assert.Equal("old", File.ReadAllText destination))

    [<Fact>]
    let ``Destination exists, running non-interactively, no explicit override given`` () =
        scratch (fun setup ->
            twoVersions setup
            let destination = outFile setup "a.txt"
            Directory.CreateDirectory(outDir setup) |> ignore
            File.WriteAllText(destination, "old")
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; destination ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("already exists", result.Errors.Replace("\n", ""))
            Assert.Contains("--force", result.Errors.Replace("\n", ""))
            Assert.Equal("old", File.ReadAllText destination))

    [<Fact>]
    let ``Both a version id and an as-of date are given`` () =
        scratch (fun setup ->
            twoVersions setup
            let destination = outFile setup "a.txt"
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--version"; "1"; "--out"; destination ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("--at and --version are mutually exclusive", result.Errors)
            Assert.False(File.Exists destination))

    [<Fact>]
    let ``Requested version is a symlink or junction entry`` () =
        scratch (fun setup ->
            Directory.CreateDirectory(Path.Combine(setup.Root, "elsewhere")) |> ignore
            createJunction (sourceFile setup "link") (Path.Combine(setup.Root, "elsewhere"))
            backupOnce setup
            let destination = outFile setup "link"
            let result = restore setup [ sourceFile setup "link"; "--version"; string (versionIds setup "link").Head; "--out"; destination ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("symlink/junction with no stored content", result.Errors.Replace("\n", ""))
            Assert.False(File.Exists destination || Directory.Exists destination))

module ``Restore never writes into the live mirror`` =
    [<Fact>]
    let ``Destination resolves inside the profile's live mirror`` () =
        scratch (fun setup ->
            twoVersions setup
            let inside = Path.Combine(setup.Target, "zz", "restored.txt")
            // A prompt would fail the test: the refusal comes first, whatever override is given.
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; inside; "--force" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("inside the profile's live mirror", result.Errors.Replace("\n", ""))
            Assert.False(File.Exists inside))

    [<Fact>]
    let ``A recursive restore's computed destination resolves inside the live mirror`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            backupOnce setup
            let directory = Path.GetDirectoryName(keyOf setup "dir\\a.txt") |> nonNull
            let inside = Path.Combine(setup.Target, "restored")
            let result = restore setup [ directory; "--recursive"; "--out"; inside; "--force" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("inside the profile's live mirror", result.Errors.Replace("\n", ""))
            Assert.False(Directory.Exists inside))

    [<Fact>]
    let ``Destination reached only through a reparse point into the mirror`` () =
        scratch (fun setup ->
            twoVersions setup
            let junction = Path.Combine(setup.Root, "elsewhere")
            createJunction junction setup.Target
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; Path.Combine(junction, "restored.txt") ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("inside the profile's live mirror", result.Errors.Replace("\n", ""))
            Assert.False(File.Exists(Path.Combine(setup.Target, "restored.txt"))))

module ``Restore writes are atomic`` =
    [<Fact>]
    let ``Restore completes successfully over an existing destination file`` () =
        scratch (fun setup ->
            twoVersions setup
            let destination = outFile setup "a.txt"
            Directory.CreateDirectory(outDir setup) |> ignore
            File.WriteAllText(destination, "old")
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; destination; "--force" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("first content", File.ReadAllText destination)
            // Nothing is left beside it.
            Assert.Equal<string list>([ destination ], Directory.GetFiles(outDir setup) |> List.ofArray))

    [<Fact>]
    let ``Restore is interrupted while writing over an existing destination file`` () =
        scratch (fun setup ->
            twoVersions setup
            let destination = outFile setup "a.txt"
            Directory.CreateDirectory(outDir setup) |> ignore
            File.WriteAllText(destination, "old")

            // The stored content of the first version goes missing: the write fails after it started.
            let hash =
                match (rowsOf setup |> List.find (fun version -> version.VersionId = (versionIds setup "a.txt").Head)).Row.Content with
                | RowContent.Regular content -> ContentHash.value content.Hash
                | RowContent.Linked _ -> failwith "regular row expected"

            let blob = Path.Combine(setup.Target, ".vara", "versions", hash.Substring(0, 2), hash)
            File.SetAttributes(blob, FileAttributes.Normal)
            File.Delete blob

            let result = restore setup [ sourceFile setup "a.txt"; "--version"; string (versionIds setup "a.txt").Head; "--out"; destination; "--force" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Equal("old", File.ReadAllText destination)
            Assert.Equal<string list>([ destination ], Directory.GetFiles(outDir setup) |> List.ofArray))

module ``Restore of a deleted file`` =
    [<Fact>]
    let ``Restoring a deleted file`` () =
        scratch (fun setup ->
            write setup "a.txt" "content before deletion"
            backupOnce setup
            changeFile setup "other.txt" "x"
            File.Delete(sourceFile setup "a.txt")
            backupOnce setup
            Assert.False(File.Exists(Path.Combine(setup.Target, keyOf setup "a.txt")))
            let destination = outFile setup "a.txt"
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--out"; destination ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("content before deletion", File.ReadAllText destination))

module ``Unknown path or version reported clearly`` =
    [<Fact>]
    let ``Path never backed up`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let result = restore setup [ "never.txt"; "--at"; "2020-01-01"; "--out"; outFile setup "x" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("No history exists for path 'never.txt'.", result.Errors.Replace("\n", "")))

    [<Fact>]
    let ``Restore without --recursive given a tracked directory path`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            backupOnce setup
            let directory = Path.GetDirectoryName(keyOf setup "dir\\a.txt") |> nonNull
            let result = restore setup [ directory; "--at"; "2099-01-01"; "--out"; outFile setup "x" ]
            Assert.Equal(1, result.ExitCode)
            let errors = result.Errors.Replace("\n", "")
            Assert.Contains("is a directory", errors)
            Assert.Contains("--recursive", errors)
            Assert.DoesNotContain("No history exists", errors))

module ``Restore to the original source location`` =
    [<Fact>]
    let ``Restoring in place to a location that no longer exists`` () =
        scratch (fun setup ->
            write setup "a.txt" "content to bring back"
            backupOnce setup
            changeFile setup "other.txt" "x"
            File.Delete(sourceFile setup "a.txt")
            backupOnce setup
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--in-place" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("content to bring back", File.ReadAllText(sourceFile setup "a.txt")))

    [<Fact>]
    let ``Restoring in place over an existing file`` () =
        scratch (fun setup ->
            twoVersions setup
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--in-place" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("already exists", result.Errors.Replace("\n", ""))
            Assert.Equal("second content, longer", File.ReadAllText(sourceFile setup "a.txt"))
            let forced = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--in-place"; "--force" ]
            Assert.Equal(0, forced.ExitCode)
            Assert.Equal("first content", File.ReadAllText(sourceFile setup "a.txt")))

    [<Fact>]
    let ``In-place restore and an explicit destination are both given`` () =
        scratch (fun setup ->
            twoVersions setup
            let result = restore setup [ sourceFile setup "a.txt"; "--at"; betweenFirstTwo setup; "--in-place"; "--out"; outFile setup "a.txt" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("--out and --in-place are mutually exclusive", result.Errors)
            Assert.False(File.Exists(outFile setup "a.txt")))

module ``Interactive version selection when restoring`` =
    [<Fact>]
    let ``Restoring interactively without a version or date`` () =
        scratch (fun setup ->
            twoVersions setup
            let script = Script(1, false)
            let destination = outFile setup "a.txt"
            // Newest first: index 1 is the first, older version.
            let result = restoreWith (interactive script.Prompts) setup [ sourceFile setup "a.txt"; "--out"; destination ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("first content", File.ReadAllText destination)
            Assert.Equal("Select a version to restore:", script.Questions[0])
            Assert.Equal(2, script.Choices[0].Length))

    [<Fact>]
    let ``Restoring non-interactively without a version or date`` () =
        scratch (fun setup ->
            twoVersions setup
            let result = restore setup [ sourceFile setup "a.txt"; "--out"; outFile setup "a.txt" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("specify either --at <date> or --version <id>", result.Errors)
            Assert.False(File.Exists(outFile setup "a.txt")))

    [<Fact>]
    let ``Selectable list excludes symlink/junction versions`` () =
        scratch (fun setup ->
            write setup "a.txt" "regular content"
            backupOnce setup
            // The path becomes a link, then goes away: history holds regular, linked and deleted versions.
            File.Delete(sourceFile setup "a.txt")
            Directory.CreateDirectory(Path.Combine(setup.Root, "elsewhere")) |> ignore
            changeFile setup "pad1.txt" "1"
            createJunction (sourceFile setup "a.txt") (Path.Combine(setup.Root, "elsewhere"))
            backupOnce setup
            Directory.Delete(sourceFile setup "a.txt")
            changeFile setup "pad2.txt" "2"
            backupOnce setup
            let script = Script(0, false)
            let result = restoreWith (interactive script.Prompts) setup [ sourceFile setup "a.txt"; "--out"; outFile setup "a.txt" ]
            Assert.Equal(0, result.ExitCode)
            // Only the regular version is offered.
            Assert.Equal(1, script.Choices[0].Length)
            Assert.Contains("Added", text (script.Choices[0][0])))

    [<Fact>]
    let ``Recursive restore without a version or date does not prompt for a version`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            backupOnce setup
            let directory = Path.GetDirectoryName(keyOf setup "dir\\a.txt") |> nonNull
            // Non-interactive: no prompt of any kind (the default prompts fail the test if one is asked).
            let result = restore setup [ directory; "--recursive"; "--out"; outDir setup; "--force" ]
            Assert.Equal(0, result.ExitCode))

module ``Restore a directory at a given date`` =
    /// dir has kept.txt (always), gone.txt (deleted after the first snapshot) and later.txt (added after it).
    let private populated (setup: Scratch) : string =
        write setup "dir\\kept.txt" "kept"
        write setup "dir\\gone.txt" "gone"
        backupOnce setup
        File.Delete(sourceFile setup "dir\\gone.txt")
        changeFile setup "dir\\later.txt" "later"
        backupOnce setup
        Path.GetDirectoryName(keyOf setup "dir\\kept.txt") |> nonNull

    [<Fact>]
    let ``Restoring a directory to a new destination`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = restore setup [ directory; "--recursive"; "--at"; betweenFirstTwo setup; "--out"; outDir setup; "--force" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("kept", File.ReadAllText(outFile setup "kept.txt"))
            Assert.Contains("2 file(s) written", result.Output))

    [<Fact>]
    let ``Restoring a directory brings back since-deleted files`` () =
        scratch (fun setup ->
            let directory = populated setup
            restore setup [ directory; "--recursive"; "--at"; betweenFirstTwo setup; "--out"; outDir setup; "--force" ] |> ignore
            Assert.Equal("gone", File.ReadAllText(outFile setup "gone.txt")))

    [<Fact>]
    let ``Restoring a directory excludes files added afterward`` () =
        scratch (fun setup ->
            let directory = populated setup
            restore setup [ directory; "--recursive"; "--at"; betweenFirstTwo setup; "--out"; outDir setup; "--force" ] |> ignore
            Assert.False(File.Exists(outFile setup "later.txt")))

    [<Fact>]
    let ``Restoring a directory removes tracked content that did not exist at that date`` () =
        scratch (fun setup ->
            let directory = populated setup
            Directory.CreateDirectory(outDir setup) |> ignore
            File.WriteAllText(outFile setup "later.txt", "stale")
            let result = restore setup [ directory; "--recursive"; "--at"; betweenFirstTwo setup; "--out"; outDir setup; "--force" ]
            Assert.Equal(0, result.ExitCode)
            Assert.False(File.Exists(outFile setup "later.txt"))
            Assert.Contains("1 file(s) removed", result.Output))

    [<Fact>]
    let ``Restoring a directory in place`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = restore setup [ directory; "--recursive"; "--at"; betweenFirstTwo setup; "--in-place"; "--force" ]
            Assert.Equal(0, result.ExitCode)
            // Each file went back to its own original location, and the file added later was removed.
            Assert.Equal("gone", File.ReadAllText(sourceFile setup "dir\\gone.txt"))
            Assert.False(File.Exists(sourceFile setup "dir\\later.txt")))

    [<Fact>]
    let ``Recursive restore combined with a specific version id`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = restore setup [ directory; "--recursive"; "--version"; "1"; "--out"; outDir setup ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("--recursive and --version are mutually exclusive", result.Errors)
            Assert.False(Directory.Exists(outDir setup)))

    [<Fact>]
    let ``Recursive restore of a directory never tracked`` () =
        scratch (fun setup ->
            populated setup |> ignore
            let result = restore setup [ "never\\tracked"; "--recursive"; "--out"; outDir setup; "--force" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("No such directory is recorded in the backup", result.Errors.Replace("\n", "")))

    [<Fact>]
    let ``Recursive restore without an explicit date`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = restore setup [ directory; "--recursive"; "--out"; outDir setup; "--force" ]
            Assert.Equal(0, result.ExitCode)
            // The current tracked state: kept and later, not gone.
            Assert.True(File.Exists(outFile setup "kept.txt"))
            Assert.True(File.Exists(outFile setup "later.txt"))
            Assert.False(File.Exists(outFile setup "gone.txt")))

    [<Fact>]
    let ``Recursive restore of a directory containing a symlink/junction`` () =
        scratch (fun setup ->
            write setup "dir\\kept.txt" "kept"
            Directory.CreateDirectory(Path.Combine(setup.Root, "elsewhere")) |> ignore
            createJunction (sourceFile setup "dir\\link") (Path.Combine(setup.Root, "elsewhere"))
            backupOnce setup
            let directory = Path.GetDirectoryName(keyOf setup "dir\\kept.txt") |> nonNull
            let result = restore setup [ directory; "--recursive"; "--out"; outDir setup; "--force" ]
            // Not a failure; reported as skipped, apart from the written and removed counts.
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("1 link(s) skipped", result.Output)
            Assert.False(File.Exists(outFile setup "link") || Directory.Exists(outFile setup "link")))

module ``Interactive and explicit snapshot selection for a directory restore`` =
    /// Snapshot 1 adds dir\a (and other\x), 2 changes dir\a, 3 touches only other\x.
    let private populated (setup: Scratch) : string =
        write setup "dir\\a.txt" "version one"
        write setup "other\\x.txt" "x1"
        backupOnce setup
        changeFile setup "dir\\a.txt" "version two, longer"
        backupOnce setup
        changeFile setup "other\\x.txt" "x2, changed"
        backupOnce setup
        Path.GetDirectoryName(keyOf setup "dir\\a.txt") |> nonNull

    [<Fact>]
    let ``Presenting the directory snapshot picker interactively`` () =
        scratch (fun setup ->
            let directory = populated setup
            let script = Script(0, true)
            let result = restoreWith (interactive script.Prompts) setup [ directory; "--recursive"; "--out"; outDir setup ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("Select a point in time to restore:", script.Questions[0])
            // The current state, then the two snapshots that touched the directory.
            Assert.Equal(3, script.Choices[0].Length)
            Assert.Equal("Current tracked state", text (script.Choices[0][0])))

    [<Fact>]
    let ``Accepting the default entry reproduces today's default behavior`` () =
        scratch (fun setup ->
            let directory = populated setup
            restoreWith (interactive (Script(0, true)).Prompts) setup [ directory; "--recursive"; "--out"; outDir setup ] |> ignore
            Assert.Equal("version two, longer", File.ReadAllText(outFile setup "a.txt")))

    [<Fact>]
    let ``Selecting a candidate snapshot restores its point in time`` () =
        scratch (fun setup ->
            let directory = populated setup
            // Newest candidate first: index 2 is snapshot 1.
            restoreWith (interactive (Script(2, true)).Prompts) setup [ directory; "--recursive"; "--out"; outDir setup ] |> ignore
            Assert.Equal("version one", File.ReadAllText(outFile setup "a.txt")))

    [<Fact>]
    let ``Only snapshots that touched the directory are offered`` () =
        scratch (fun setup ->
            let directory = populated setup
            let script = Script(0, true)
            restoreWith (interactive script.Prompts) setup [ directory; "--recursive"; "--out"; outDir setup ] |> ignore
            let labels = script.Choices[0] |> List.map text
            Assert.DoesNotContain(labels, fun label -> label.StartsWith "#3"))

    [<Fact>]
    let ``Running and failed snapshots are never offered`` () =
        scratch (fun setup ->
            let directory = populated setup
            setStatus setup 1L "Failed"
            setStatus setup 2L "Running"
            let script = Script(0, true)
            restoreWith (interactive script.Prompts) setup [ directory; "--recursive"; "--out"; outDir setup ] |> ignore
            Assert.Equal(1, script.Choices[0].Length))

    [<Fact>]
    let ``Cancelled snapshots are visually distinguished`` () =
        scratch (fun setup ->
            let directory = populated setup
            setStatus setup 2L "Cancelled"
            let script = Script(0, true)
            restoreWith (interactive script.Prompts) setup [ directory; "--recursive"; "--out"; outDir setup ] |> ignore
            let toneOf (index: int) = script.Choices[0][index] |> List.map (fun span -> span.Tone)
            // Index 1 is snapshot 2 (cancelled), index 2 is snapshot 1 (complete).
            Assert.Contains(Tone.PartialFailure, toneOf 1)
            Assert.Contains(Tone.Success, toneOf 2)
            Assert.DoesNotContain(Tone.PartialFailure, toneOf 2))

    [<Fact>]
    let ``Picker entries report directory-scoped statistics`` () =
        scratch (fun setup ->
            let directory = populated setup
            let script = Script(0, true)
            restoreWith (interactive script.Prompts) setup [ directory; "--recursive"; "--out"; outDir setup ] |> ignore
            let second = text (script.Choices[0][1])
            Assert.Contains("+0/~1/→0/-0", second)
            Assert.Contains("1 file(s)", second))

    [<Fact>]
    let ``Selecting a specific snapshot non-interactively`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = restore setup [ directory; "--recursive"; "--snapshot"; "1"; "--out"; outDir setup; "--force" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("version one", File.ReadAllText(outFile setup "a.txt")))

    [<Fact>]
    let ``--snapshot and --at are mutually exclusive`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = restore setup [ directory; "--recursive"; "--snapshot"; "1"; "--at"; "2020-01-01"; "--out"; outDir setup; "--force" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("--snapshot and --at are mutually exclusive", result.Errors)
            Assert.False(Directory.Exists(outDir setup)))

    [<Fact>]
    let ``--snapshot without --recursive is rejected`` () =
        scratch (fun setup ->
            populated setup |> ignore
            let result = restore setup [ sourceFile setup "dir\\a.txt"; "--snapshot"; "1"; "--out"; outFile setup "a.txt" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("--snapshot only applies to a recursive directory restore", result.Errors.Replace("\n", "")))

    [<Fact>]
    let ``--snapshot naming a snapshot that never touched the directory`` () =
        scratch (fun setup ->
            let directory = populated setup
            let untouched = restore setup [ directory; "--recursive"; "--snapshot"; "3"; "--out"; outDir setup; "--force" ]
            Assert.Equal(1, untouched.ExitCode)
            Assert.Contains("is not a valid candidate", untouched.Errors.Replace("\n", ""))
            let unknown = restore setup [ directory; "--recursive"; "--snapshot"; "99"; "--out"; outDir setup; "--force" ]
            Assert.Equal(1, unknown.ExitCode)
            Assert.False(Directory.Exists(outDir setup)))

module ``Single confirmation for a directory restore`` =
    let private populated (setup: Scratch) : string =
        write setup "dir\\a.txt" "alpha"
        write setup "dir\\b.txt" "beta"
        backupOnce setup
        changeFile setup "dir\\c.txt" "gamma"
        backupOnce setup
        Path.GetDirectoryName(keyOf setup "dir\\a.txt") |> nonNull

    [<Fact>]
    let ``Interactive confirmation before a directory restore`` () =
        scratch (fun setup ->
            let directory = populated setup
            Directory.CreateDirectory(outDir setup) |> ignore
            File.WriteAllText(outFile setup "c.txt", "stale")
            let script = Script(0, true)
            let result = restoreWith (interactive script.Prompts) setup [ directory; "--recursive"; "--at"; betweenFirstTwo setup; "--out"; outDir setup ]
            Assert.Equal(0, result.ExitCode)
            // Exactly one confirmation, reporting what will be written and removed.
            let confirmations = script.Questions |> Seq.filter (fun question -> question.StartsWith "This will write") |> List.ofSeq
            Assert.Single confirmations |> ignore
            Assert.Contains("write 2 file(s) and remove 1 file(s)", confirmations.Head))

    [<Fact>]
    let ``Explicit override skips the confirmation`` () =
        scratch (fun setup ->
            let directory = populated setup
            // The default prompts fail the test if anything is asked.
            let result = restore setup [ directory; "--recursive"; "--at"; betweenFirstTwo setup; "--out"; outDir setup; "--force" ]
            Assert.Equal(0, result.ExitCode)
            Assert.True(File.Exists(outFile setup "a.txt")))

    [<Fact>]
    let ``User declines the directory restore confirmation`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = restoreWith (interactive (Script(0, false)).Prompts) setup [ directory; "--recursive"; "--at"; betweenFirstTwo setup; "--out"; outDir setup ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Restore cancelled", result.Output)
            Assert.False(Directory.Exists(outDir setup)))

    [<Fact>]
    let ``Non-interactive session without an explicit override`` () =
        scratch (fun setup ->
            let directory = populated setup
            let result = restore setup [ directory; "--recursive"; "--at"; betweenFirstTwo setup; "--out"; outDir setup ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("--force", result.Errors.Replace("\n", ""))
            Assert.False(Directory.Exists(outDir setup)))

module ``Graceful cancellation of a directory restore via CtrlC`` =
    [<Fact>]
    let ``Single Ctrl+C during a directory restore's writes`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            write setup "dir\\b.txt" "beta"
            backupOnce setup
            let directory = Path.GetDirectoryName(keyOf setup "dir\\a.txt") |> nonNull
            use source = new CancellationTokenSource()
            source.Cancel()
            let result = invokeEnv id source.Token (Array.ofList ([ "restore"; directory; "--recursive"; "--out"; outDir setup; "--force"; "--profile"; "docs"; "--config"; setup.Config ]))
            // Stopped before the first planned path: reported as cancelled, with the partial-failure code.
            Assert.Equal(2, result.ExitCode)
            Assert.Contains("cancelled", result.Output)
            Assert.False(File.Exists(outFile setup "a.txt")))

    [<Fact>]
    let ``Second Ctrl+C forces immediate termination`` () =
        let forced = ref None
        let cancellation = GracefulCancellation(fun code -> forced.Value <- Some code)
        Assert.True(cancellation.RequestCancellation())
        Assert.Equal(None, forced.Value)
        Assert.False(cancellation.RequestCancellation())
        Assert.Equal(Some(Outcome.exitCode Outcome.HardError), forced.Value)
