/// Tests of openspec/specs/portable-distribution/spec.md. A module is named after a requirement and a test after one of its
/// scenarios.
module Vara.Cli.Tests.PortableDistributionTests

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Xunit
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests

/// The repository's root: the directory above the test binaries that holds `Vara.slnx`.
let private repositoryRoot: string =
    let rec up (directory: DirectoryInfo) : string =
        if File.Exists(Path.Combine(directory.FullName, "Vara.slnx")) then
            directory.FullName
        else
            up (directory.Parent |> nonNull)

    up (DirectoryInfo AppContext.BaseDirectory)

/// Runs an executable with arguments and returns its exit code and output.
let private runExe (exe: string) (arguments: string list) : int * string =
    let info = ProcessStartInfo exe

    for argument in arguments do
        info.ArgumentList.Add argument

    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use proc = Process.Start info |> nonNull
    let err = proc.StandardError.ReadToEndAsync()
    let out = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, out + err.GetAwaiter().GetResult()

/// The published executable, when `dotnet publish src/Vara.Cli -c Release -r win-x64 -o artifacts/cli` has been run.
let private publishedExe: string option =
    let path = Path.Combine(repositoryRoot, "artifacts", "cli", "vara.exe")
    if File.Exists path then Some path else None

/// Files that would be a native SQLite library of their own, beside the managed assemblies.
let private companionNativeLibraries (directory: string) : string list =
    Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
    |> Seq.map Path.GetFileName
    |> Seq.filter (fun name ->
        Regex.IsMatch(name, @"^(lib)?e?_?sqlite3.*\.(dll|so|dylib)$", RegexOptions.IgnoreCase))
    |> List.ofSeq

module ``No companion native SQLite library`` =
    [<Fact>]
    let ``Running the executable alone in a directory`` () =
        // The build deploys no native SQLite library, and the backup below (which touches the snapshot repository)
        // completes: the SQLite functionality is resolved from the operating system's `winsqlite3.dll`.
        Assert.Empty(companionNativeLibraries AppContext.BaseDirectory)

        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            Assert.Equal(1, (snapshotsOf setup).Length))

        // The published executable, alone in an empty directory, does the same for a backup, a snapshot listing and a restore.
        match publishedExe with
        | None -> ()
        | Some published ->
            scratch (fun setup ->
                let directory = Path.Combine(setup.Root, "alone")
                Directory.CreateDirectory directory |> ignore
                let exe = Path.Combine(directory, "vara.exe")
                File.Copy(published, exe)
                Assert.Equal<string list>([ "vara.exe" ], Directory.GetFileSystemEntries directory |> Array.map Path.GetFileName |> List.ofArray)

                write setup "a.txt" "alpha"
                let backupCode, _ = runExe exe [ "backup"; "docs"; "--config"; setup.Config ]
                Assert.Equal(0, backupCode)

                let listCode, listing = runExe exe [ "snapshots"; "docs"; "--config"; setup.Config ]
                Assert.Equal(0, listCode)
                Assert.Contains("Complete", listing)

                let restored = Path.Combine(setup.Root, "restored.txt")

                let restoreCode, _ =
                    runExe exe [ "restore"; sourceFile setup "a.txt"; "--at"; "2100-01-01"; "--out"; restored; "--profile"; "docs"; "--config"; setup.Config ]

                Assert.Equal(0, restoreCode)
                Assert.Equal("alpha", File.ReadAllText restored)
                Assert.Equal<string list>([ "vara.exe" ], Directory.GetFileSystemEntries directory |> Array.map Path.GetFileName |> List.ofArray))

module ``Documented minimum OS boundary`` =
    /// The README with its line breaks and runs of spaces collapsed, so that a sentence can be searched for as written.
    let private readme: string =
        Regex.Replace(File.ReadAllText(Path.Combine(repositoryRoot, "README.md")), @"\s+", " ")

    [<Fact>]
    let ``Unsupported Windows version`` () =
        // The README states the versions that provide the OS-supplied system SQLite library, as the spec words them ...
        Assert.Contains("Windows 10, version 1903 or later", readme)
        Assert.Contains("Windows 11", readme)
        Assert.Contains("Windows Server 2022 or later", readme)
        // ... and that an older Windows version, or another operating system, cannot run the executable because it lacks
        // that library, which is the documented limitation that a failure to initialize SQLite on them comes from.
        Assert.Contains("Older Windows versions, and non-Windows operating systems, do not provide `winsqlite3.dll` and cannot run `vara.exe`", readme)
