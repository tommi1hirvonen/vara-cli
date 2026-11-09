/// End-to-end scenario tests of the composed backup run: a real source, target, manifest and content store.
/// Each test is named after its scenario in openspec/specs/backup-execution/spec.md.
module Vara.Engine.Tests.BackupScenarioTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Vara.Domain
open Vara.Engine
open Vara.Engine.Tests
open Vara.Engine.Tests.BackupRunTests

let private sourceOf (path: string) : Source =
    { Path = absolute path
      Recursive = true
      Excludes = []
      IncludeGlobs = []
      ExcludeGlobs = [] }

let private profileFor (setup: Setup) (sources: Source list) : Profile =
    match Profile.create "p" (absolute setup.TargetDir) sources None None with
    | Ok profile -> profile
    | Error _ -> failwith "invalid profile"

let private runCmd (arguments: string) =
    let info = ProcessStartInfo("cmd.exe", $"/c {arguments}")
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    use proc = Process.Start info |> nonNull
    proc.WaitForExit()
    proc.ExitCode

module ``One-to-one mirror of source state`` =
    [<Fact>]
    let ``File added to source`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            write setup "b.txt" "beta" |> ignore
            runOk quiet profile |> ignore
            Assert.Equal("beta", File.ReadAllText(mirrorFile setup "b.txt")))

    [<Fact>]
    let ``File removed from source`` () =
        within (fun setup ->
            let doomed = write setup "doomed.txt" "alpha"
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            Assert.True(File.Exists(mirrorFile setup "doomed.txt"))
            File.Delete doomed
            runOk quiet profile |> ignore
            Assert.False(File.Exists(mirrorFile setup "doomed.txt")))

    [<Fact>]
    let ``Two sources share a subfolder or file name at the same depth`` () =
        within (fun setup ->
            let first = Path.Combine(setup.Root, "first")
            let second = Path.Combine(setup.Root, "second")
            TempDir.write (Path.Combine(first, "data", "same.txt")) "from first"
            TempDir.write (Path.Combine(second, "data", "same.txt")) "from second"
            let result = runOk quiet (profileFor setup [ sourceOf first; sourceOf second ])
            Assert.Equal(2, result.Totals.FilesAdded)

            // Each entry is mirrored under its own full path, so neither replaced the other.
            let mirrored (source: string) =
                let key = ScanRules.toMirrorPath (Path.Combine(source, "data", "same.txt")) |> ValueOption.get |> MirrorPath.value
                File.ReadAllText(Path.Combine(setup.TargetDir, key))

            Assert.Equal("from first", mirrored first)
            Assert.Equal("from second", mirrored second))

    [<Fact>]
    let ``Drive-letter-root source is scanned from the actual drive root`` () =
        within (fun setup ->
            write setup "marker.txt" "root" |> ignore
            let used = DriveInfo.GetDrives() |> Array.map (fun d -> Char.ToUpperInvariant d.Name[0]) |> Set.ofArray
            // Each suite starts its letter search at a different letter, so tests running in parallel never take the same one.
            let letter = [ for code in int 'Q' .. -1 .. int 'D' -> char code ] |> List.find (fun c -> not (used.Contains c))
            Assert.Equal(0, runCmd $"subst {letter}: \"{setup.SourceDir}\"")

            try
                let result = runOk quiet (profileFor setup [ sourceOf $"{letter}:\\" ])
                Assert.Equal(1, result.Totals.FilesAdded)
                // Mirrored under the drive-letter segment of the target root.
                Assert.Equal("root", File.ReadAllText(Path.Combine(setup.TargetDir, string letter, "marker.txt")))
            finally
                runCmd $"subst {letter}: /d" |> ignore)

    [<Fact>]
    let ``UNC source path produces no mirror entries`` () =
        within (fun setup ->
            // An unreachable share is an unavailable source: the run reports it and writes nothing anywhere.
            let unc = @"\\vara-test-unreachable-host\share\file.txt"
            let result = runOk quiet (profileFor setup [ sourceOf unc ])
            Assert.Equal(Outcome.PartialFailure, Run.outcome result)
            Assert.Equal<string list>([ unc ], result.FailedPaths)
            Assert.Equal(0, result.Totals.FilesAdded)

            let mirrored =
                Directory.EnumerateFiles(setup.TargetDir, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun file -> not (file.Contains ".vara"))

            Assert.Empty mirrored)

module ``Version history retained on change`` =
    [<Fact>]
    let ``Overwritten file remains recoverable`` () =
        within (fun setup ->
            let path = write setup "a.txt" "first content"
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            File.WriteAllText(path, "second, longer content")
            runOk quiet profile |> ignore

            // The mirror holds the current content; the previous content is still stored and recorded.
            Assert.Equal("second, longer content", File.ReadAllText(mirrorFile setup "a.txt"))
            let key = mirrorKey setup "a.txt"
            let versions = query setup $"SELECT content_hash, change_kind FROM file_versions WHERE relative_path = '{key}' ORDER BY id"
            Assert.Equal(2, List.length versions)
            let previousHash = versions.Head.Split('|').[0]
            let blob = Path.Combine(setup.TargetDir, ".vara", "versions", previousHash.Substring(0, 2), previousHash)
            Assert.Equal("first content", File.ReadAllText blob))

module ``Symlinks and junctions are not followed`` =
    let private junction (setup: Setup) (name: string) (target: string) =
        InteropTests.createJunction (Path.Combine(setup.SourceDir, name)) target

    [<Fact>]
    let ``Previously tracked file replaced by a symlink`` () =
        within (fun setup ->
            let path = write setup "item" "regular file content"
            let outside = Path.Combine(setup.Root, "outside")
            TempDir.write (Path.Combine(outside, "inside.txt")) "never copied"
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore

            File.Delete path
            junction setup "item" outside
            let result = runOk quiet profile
            // Not a deletion: the path is now recorded as a link, whose target is kept apart from any content hash.
            Assert.Equal(0, result.Totals.FilesDeleted)
            let key = mirrorKey setup "item"
            let rows = query setup $"SELECT change_kind, ifnull(content_hash,'~'), ifnull(link_target,'~') FROM file_versions WHERE relative_path = '{key}' ORDER BY id"
            Assert.Equal(2, List.length rows)
            Assert.StartsWith("Linked|~|", rows.[1])
            Assert.Contains("outside", rows.[1]))

    [<Fact>]
    let ``Symlink present across multiple runs`` () =
        within (fun setup ->
            let outside = Path.Combine(setup.Root, "outside")
            TempDir.write (Path.Combine(outside, "inside.txt")) "never copied"
            junction setup "link" outside
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            let second = runOk quiet profile
            let changes = second.Totals.FilesAdded + second.Totals.FilesChanged + second.Totals.FilesMoved + second.Totals.FilesDeleted
            Assert.Equal(0, changes)
            Assert.Equal(1, List.length (query setup "SELECT id FROM file_versions")))

    [<Fact>]
    let ``Mirror copy removed when a tracked file becomes a symlink`` () =
        within (fun setup ->
            let path = write setup "item" "regular file content"
            let outside = Path.Combine(setup.Root, "outside")
            Directory.CreateDirectory outside |> ignore
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            Assert.True(File.Exists(mirrorFile setup "item"))

            File.Delete path
            junction setup "item" outside
            runOk quiet profile |> ignore
            Assert.False(File.Exists(mirrorFile setup "item")))

module ``Unreadable files do not abort the run`` =
    [<Fact>]
    let ``Deletion suppressed for files under a permission-denied directory`` () =
        within (fun setup ->
            write setup "sub\\kept.txt" "kept" |> ignore
            write setup "other.txt" "other" |> ignore
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            let rows = query setup "SELECT COUNT(*) FROM file_versions"
            let subDirectory = Path.Combine(setup.SourceDir, "sub")
            runCmd $"icacls \"{subDirectory}\" /deny \"%%USERNAME%%:(RD)\"" |> ignore

            try
                let result = runOk quiet profile
                Assert.Equal(0, result.Totals.FilesDeleted)
                Assert.Equal<string list>([ "sub" ], result.FailedPaths)
                // Mirror entry and manifest state are exactly as before.
                Assert.True(File.Exists(mirrorFile setup "sub\\kept.txt"))
                Assert.Equal<string list>(rows, query setup "SELECT COUNT(*) FROM file_versions")
            finally
                runCmd $"icacls \"{subDirectory}\" /remove:d \"%%USERNAME%%\"" |> ignore)

    [<Fact>]
    let ``Deletion suppressed for a locked or permission-denied file`` () =
        within (fun setup ->
            let path = write setup "locked.txt" "before"
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            File.WriteAllText(path, "after, longer")
            let rows = query setup "SELECT COUNT(*) FROM file_versions"
            use hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)
            let result = runOk quiet profile
            // The file cannot be read: it is reported as failed, not deleted, and nothing about it changed.
            Assert.Equal(0, result.Totals.FilesDeleted)
            Assert.Equal(1, result.Totals.FilesFailed)
            Assert.Equal("before", File.ReadAllText(mirrorFile setup "locked.txt"))
            Assert.Equal<string list>(rows, query setup "SELECT COUNT(*) FROM file_versions"))

    [<Fact>]
    let ``Configured source path does not exist`` () =
        within (fun setup ->
            write setup "a.txt" "alpha" |> ignore
            let profile = profileOf setup "p" None
            runOk quiet profile |> ignore
            // The source disappears (an unplugged drive): its files stay in the mirror and the manifest.
            Directory.Delete(setup.SourceDir, true)
            let result = runOk quiet profile
            Assert.Equal(0, result.Totals.FilesDeleted)
            Assert.Equal(1, List.length result.FailedPaths)
            Assert.True(File.Exists(mirrorFile setup "a.txt")))
