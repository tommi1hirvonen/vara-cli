module Vara.Engine.Tests.ScannerTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Vara.Domain
open Vara.Engine
open Vara.Engine.Tests

let source (path: string) : Source =
    { Path = (AbsolutePath.tryCreate path).Value
      Recursive = true
      Excludes = []
      IncludeGlobs = []
      ExcludeGlobs = [] }

let private scan (sources: Source list) : ScanEntry list = Scanner.scan sources |> Seq.toList

let private files (entries: ScanEntry list) : string list =
    entries
    |> List.choose (function
        | ScanEntry.File file -> Some(MirrorPath.value file.FilePath)
        | _ -> None)
    |> List.sort

let private links (entries: ScanEntry list) : string list =
    entries
    |> List.choose (function
        | ScanEntry.Link link -> Some(MirrorPath.value link.LinkPath)
        | _ -> None)

let private failures (entries: ScanEntry list) : ScanFailure list =
    entries
    |> List.choose (function
        | ScanEntry.Failure failure -> Some failure
        | _ -> None)

/// The mirror path an entry at root + segments has.
let private mirrorOf (root: string) (relative: string) : string =
    ScanRules.toMirrorPath (Path.Combine(root, relative)) |> ValueOption.get |> MirrorPath.value

let private runCmd (arguments: string) =
    let info = ProcessStartInfo("cmd.exe", $"/c {arguments}")
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use proc = Process.Start info |> nonNull
    let error = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    Assert.True((proc.ExitCode = 0), $"cmd /c {arguments} failed: {error}")

/// Denies the current user the given icacls rights on a path, runs the body, and restores access.
let private denied (path: string) (rights: string) (body: unit -> unit) =
    runCmd $"icacls \"{path}\" /deny \"%%USERNAME%%:({rights})\""

    try
        body ()
    finally
        runCmd $"icacls \"{path}\" /remove:d \"%%USERNAME%%\""

module ``Source exclusion rules honored`` =
    [<Fact>]
    let ``Excluded subfolder`` () =
        TempDir.using (fun root ->
            TempDir.write (Path.Combine(root, "keep.txt")) "k"
            TempDir.write (Path.Combine(root, "excluded", "skip.txt")) "s"
            let entries = scan [ { source root with Excludes = [ "excluded" ] } ]
            Assert.Equal<string list>([ mirrorOf root "keep.txt" ], files entries))

    [<Fact>]
    let ``Nested exclude written with the non-native path separator`` () =
        TempDir.using (fun root ->
            TempDir.write (Path.Combine(root, "keep.txt")) "k"
            TempDir.write (Path.Combine(root, "sub", "excluded", "skip.txt")) "s"
            let forward = scan [ { source root with Excludes = [ "sub/excluded" ] } ]
            let backward = scan [ { source root with Excludes = [ "sub\\excluded" ] } ]
            Assert.Equal<string list>([ mirrorOf root "keep.txt" ], files forward)
            Assert.Equal<string list>([ mirrorOf root "keep.txt" ], files backward))

    [<Fact>]
    let ``Excluded directory is never traversed`` () =
        TempDir.using (fun root ->
            TempDir.write (Path.Combine(root, "keep.txt")) "k"
            TempDir.write (Path.Combine(root, "excluded", "skip.txt")) "s"

            // Listing the excluded directory would fail here; no failure means it was never enumerated.
            denied (Path.Combine(root, "excluded")) "RD" (fun () ->
                let entries = scan [ { source root with Excludes = [ "excluded" ] } ]
                Assert.Empty(failures entries)
                Assert.Equal<string list>([ mirrorOf root "keep.txt" ], files entries)))

    [<Fact>]
    let ``Excluded directory contains unreadable content`` () =
        TempDir.using (fun root ->
            TempDir.write (Path.Combine(root, "keep.txt")) "k"
            let nested = Path.Combine(root, "excluded", "denied-nested")
            TempDir.write (Path.Combine(nested, "hidden.txt")) "h"

            denied nested "RD" (fun () ->
                let entries = scan [ { source root with Excludes = [ "excluded" ] } ]
                Assert.Empty(failures entries)
                Assert.Equal<string list>([ mirrorOf root "keep.txt" ], files entries)))

    [<Fact>]
    let ``Globs filter files without affecting directory exclusion`` () =
        TempDir.using (fun root ->
            TempDir.write (Path.Combine(root, "a.txt")) "a"
            TempDir.write (Path.Combine(root, "a.log")) "a"
            TempDir.write (Path.Combine(root, "sub", "b.txt")) "b"
            TempDir.write (Path.Combine(root, "sub", "b.log")) "b"
            TempDir.write (Path.Combine(root, "excluded", "skip.txt")) "s"

            let entries =
                scan [ { source root with Excludes = [ "excluded" ]; IncludeGlobs = [ "**/*.txt" ] } ]

            Assert.Equal<string list>([ mirrorOf root "a.txt"; mirrorOf root "sub\\b.txt" ] |> List.sort, files entries)

            let withExclude = scan [ { source root with ExcludeGlobs = [ "**/*.log" ] } ]
            Assert.DoesNotContain(files withExclude, fun f -> f.EndsWith ".log"))

    [<Fact>]
    let ``A non-recursive source scans only the top level`` () =
        TempDir.using (fun root ->
            TempDir.write (Path.Combine(root, "a.txt")) "a"
            TempDir.write (Path.Combine(root, "sub", "b.txt")) "b"
            let entries = scan [ { source root with Recursive = false } ]
            Assert.Equal<string list>([ mirrorOf root "a.txt" ], files entries))

module ``One-to-one mirror of source state`` =
    [<Fact>]
    let ``Scanning gives each entry its path under its own drive letter`` () =
        TempDir.using (fun root ->
            TempDir.write (Path.Combine(root, "a.txt")) "a"
            TempDir.write (Path.Combine(root, "sub", "b.txt")) "b"
            let entries = scan [ source root ]
            Assert.Equal<string list>([ mirrorOf root "a.txt"; mirrorOf root "sub\\b.txt" ] |> List.sort, files entries))

    [<Fact>]
    let ``A source pointing directly at a single file scans just that file`` () =
        TempDir.using (fun root ->
            let file = Path.Combine(root, "single.txt")
            TempDir.write file "x"
            TempDir.write (Path.Combine(root, "other.txt")) "y"
            let entries = scan [ source file ]
            Assert.Equal<string list>([ mirrorOf root "single.txt" ], files entries))

    [<Fact>]
    let ``Drive-letter-root source is scanned from the actual drive root`` () =
        TempDir.using (fun root ->
            TempDir.write (Path.Combine(root, "marker.txt")) "root"
            TempDir.write (Path.Combine(root, "decoy", "decoyfile.txt")) "decoy"

            let used = DriveInfo.GetDrives() |> Array.map (fun d -> Char.ToUpperInvariant d.Name[0]) |> Set.ofArray
            // Each suite starts its letter search at a different letter, so tests running in parallel never take the same one.
            let letter = [ for code in int 'X' .. -1 .. int 'D' -> char code ] |> List.find (fun c -> not (used.Contains c))
            runCmd $"subst {letter}: \"{root}\""
            let originalDirectory = Environment.CurrentDirectory

            try
                // A bare "L:" resolves to the drive's current directory, here the decoy folder; "L:\" must not.
                Directory.SetCurrentDirectory $"{letter}:\\decoy"
                let entries = scan [ source $"{letter}:\\" ]
                Assert.Equal<string list>([ $"{letter}\\decoy\\decoyfile.txt"; $"{letter}\\marker.txt" ], files entries)
            finally
                Directory.SetCurrentDirectory originalDirectory
                runCmd $"subst {letter}: /d")

module ``Unreadable files do not abort the run`` =
    [<Fact>]
    let ``Permission-denied directory encountered during scanning`` () =
        TempDir.using (fun root ->
            TempDir.write (Path.Combine(root, "keep.txt")) "k"
            let deniedDirectory = Path.Combine(root, "denied-dir")
            TempDir.write (Path.Combine(deniedDirectory, "hidden.txt")) "h"

            denied deniedDirectory "RD" (fun () ->
                let entries = scan [ source root ]
                Assert.Equal<string list>([ mirrorOf root "keep.txt" ], files entries)
                // One failure for the subtree's root, not one per file inside it.
                let failure = Assert.Single(failures entries)
                Assert.Equal("denied-dir", failure.Reported)
                Assert.Equal(ScanFailureReason.UnreadableDirectory, failure.Reason)
                Assert.Equal(mirrorOf root "denied-dir", MirrorPath.value failure.Covers)))

    [<Fact>]
    let ``Configured source path does not exist`` () =
        TempDir.using (fun root ->
            let missing = Path.Combine(root, "does-not-exist")
            let entries = scan [ source missing ]
            let failure = Assert.Single(failures entries)
            Assert.Equal(1, List.length entries)
            Assert.Equal(missing, failure.Reported)
            Assert.Equal(ScanFailureReason.SourceUnavailable, failure.Reason)
            Assert.Equal(mirrorOf missing "", MirrorPath.value failure.Covers))

    [<Fact>]
    let ``An unreachable UNC source is an unavailable source, covered by its own path`` () =
        let unc = @"\\vara-test-unreachable-host\share\file.txt"
        let failure = Assert.Single(failures (scan [ source unc ]))
        Assert.Equal(ScanFailureReason.SourceUnavailable, failure.Reason)
        Assert.Equal(unc, MirrorPath.value failure.Covers)

    [<Fact>]
    let ``Scanning continues after a failure and reports it next to the entries`` () =
        TempDir.using (fun root ->
            let missing = Path.Combine(root, "missing")
            TempDir.write (Path.Combine(root, "real", "a.txt")) "a"
            let entries = scan [ source missing; source (Path.Combine(root, "real")) ]
            Assert.Equal(1, List.length (failures entries))
            Assert.Equal(1, List.length (files entries))
            // The failure arrives before the entries of the later source.
            Assert.True(match entries with [ ScanEntry.Failure _; ScanEntry.File _ ] -> true | _ -> false))

module ``Symlinks and junctions are not followed`` =
    [<Fact>]
    let ``Symlink encountered during scan`` () =
        TempDir.using (fun root ->
            let outside = Path.Combine(root, "outside")
            TempDir.write (Path.Combine(outside, "hidden-behind-link.txt")) "h"
            let scanned = Path.Combine(root, "scanned")
            TempDir.write (Path.Combine(scanned, "a.txt")) "a"
            InteropTests.createJunction (Path.Combine(scanned, "linked-dir")) outside

            let entries = scan [ source scanned ]
            // The link is reported with its target, and nothing behind it is traversed.
            Assert.Equal<string list>([ mirrorOf scanned "linked-dir" ], links entries)
            Assert.Equal<string list>([ mirrorOf scanned "a.txt" ], files entries)

            match entries |> List.tryPick (function ScanEntry.Link link -> Some link | _ -> None) with
            | Some link -> Assert.Equal(ValueSome outside, link.LinkTarget |> ValueOption.map (fun t -> t.TrimEnd '\\'))
            | None -> Assert.Fail "expected a link")

    [<Fact>]
    let ``An excluded junction is still filtered out`` () =
        TempDir.using (fun root ->
            let outside = Path.Combine(root, "outside")
            TempDir.write (Path.Combine(outside, "x.txt")) "x"
            let scanned = Path.Combine(root, "scanned")
            Directory.CreateDirectory scanned |> ignore
            InteropTests.createJunction (Path.Combine(scanned, "linked-dir")) outside
            let entries = scan [ { source scanned with Excludes = [ "linked-dir" ] } ]
            Assert.Empty entries)
