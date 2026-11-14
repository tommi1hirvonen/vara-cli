/// Tests of the commands against a manifest written by a newer version. The module is named after a requirement of
/// openspec/specs/manifest-versioning/spec.md and each test after one of its scenarios.
module Vara.Cli.Tests.ManifestVersionCommandTests

open System.IO
open Microsoft.Data.Sqlite
open Xunit
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests
open Vara.Engine

/// Records a schema version in the target's manifest, as a newer vara would have.
let stampManifest (setup: Scratch) (version: int) : unit =
    use connection = new SqliteConnection $"Data Source={SqliteManifest.pathIn setup.Target};Pooling=False"
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA user_version = {version};"
    command.ExecuteNonQuery() |> ignore

/// Every file of the target with its length, so that any write shows.
let private targetState (setup: Scratch) : (string * int64) list =
    Directory.EnumerateFiles(setup.Target, "*", SearchOption.AllDirectories)
    |> Seq.map (fun path -> path, FileInfo(path).Length)
    |> Seq.sort
    |> List.ofSeq

module ``A newer manifest is refused`` =
    [<Fact>]
    let ``Backup refuses a newer manifest`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            stampManifest setup 99
            let before = targetState setup
            write setup "b.txt" "bravo"

            let result = invoke [| "backup"; "docs"; "--config"; setup.Config |]

            Assert.Equal(1, result.ExitCode)
            Assert.Contains("version 99", result.Errors)
            Assert.Contains("supports up to version 1", result.Errors)
            Assert.DoesNotContain("Unhandled exception", result.Errors)
            Assert.Equal<(string * int64) list>(before, targetState setup))

    [<Fact>]
    let ``Read-only command refuses a newer manifest`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            stampManifest setup 99
            let before = File.ReadAllBytes(SqliteManifest.pathIn setup.Target)

            let result = run setup [ "history"; sourceFile setup "a.txt" ]

            Assert.Equal(1, result.ExitCode)
            Assert.Contains("version 99", result.Errors)
            Assert.DoesNotContain("Unhandled exception", result.Errors)
            Assert.Equal<byte array>(before, File.ReadAllBytes(SqliteManifest.pathIn setup.Target)))
