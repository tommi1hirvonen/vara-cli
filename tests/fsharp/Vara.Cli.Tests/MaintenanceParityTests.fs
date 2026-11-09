/// Runs `prune` and `check` of the C# and F# builds, as processes, on copies of one target that the F# backup wrote
/// (with snapshots at several times, so that the retention tiers have something to decide), and compares what a script
/// can see: the snapshot ids and blob names that remain after a prune, the findings and exit codes of a check, and the
/// refusal for a missing policy. Rows and counts are compared, not layout.
///
/// Not covered here, on purpose: the interactive flows (a child process cannot be driven through a prompt, so they are
/// tested in process with scripted prompts), and cancellation (a graceful Ctrl+C cannot be delivered to a child
/// process, so it rests on the engine tests and the in-process command tests).
module Vara.Cli.Tests.MaintenanceParityTests

open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.Data.Sqlite
open Xunit
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.CommandParityTests
open Vara.Cli.Tests.HistoryCommandTests
open Vara.Cli.Tests.PruneCommandTests
open Vara.Engine

let private copyDirectory (source: string) (destination: string) : unit =
    Directory.CreateDirectory destination |> ignore

    for directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories) do
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory))) |> ignore

    for file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
        File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)))

/// Two copies of the target the F# backup wrote, each with a configuration of its own.
type private Pair =
    { Setup: Scratch
      CsTarget: string
      FsTarget: string
      CsConfig: string
      FsConfig: string }

/// Six snapshots of a.txt, each with content of its own, dated so that the policy below keeps some and not others:
/// two on 2026-01-05, one in February, one on 2026-03-03 and two on 2026-03-04.
let private dates =
    [ 1, "2026-01-05T10:00:00.0000000+00:00"
      2, "2026-01-05T18:00:00.0000000+00:00"
      3, "2026-02-10T12:00:00.0000000+00:00"
      4, "2026-03-03T09:00:00.0000000+00:00"
      5, "2026-03-04T09:00:00.0000000+00:00"
      6, "2026-03-04T17:00:00.0000000+00:00" ]

/// A connection string for the manifest of a target, not pooled, so that nothing keeps the file open afterwards.
let private connectionString (target: string) : string =
    let database = Path.Combine(target, ".vara", "profile.db")
    $"Data Source={database};Pooling=False"

let private datedTarget (setup: Scratch) : unit =
    write setup "a.txt" "content one"
    backupOnce setup

    for version in [ "content two, longer"; "content three, longer still"; "content four, longer than before"; "content five, longer than four was"; "content six, the longest content of them all" ] do
        changeFile setup "a.txt" version
        backupOnce setup

    SqliteConnection.ClearAllPools()
    use connection = new SqliteConnection(connectionString setup.Target)
    connection.Open()

    for id, date in dates do
        use command = connection.CreateCommand()
        command.CommandText <- $"UPDATE snapshots SET started_at = '{date}', completed_at = '{date}' WHERE id = {id}"
        command.ExecuteNonQuery() |> ignore

/// `keep_daily: 2` keeps 03-04's newest and 03-03's; `keep_monthly: 3` keeps March's, February's and January's newest.
/// That keeps snapshots 6, 4, 3 and 2, and leaves 1 and 5 eligible.
let private policyYaml (target: string) (source: string) (policy: bool) : string =
    let retention = if policy then "    retention:\n      keep_daily: 2\n      keep_monthly: 3\n" else ""
    $"profiles:\n  - name: docs\n    target: {target}\n    sources:\n      - path: {source}\n{retention}"

let private withPair (policy: bool) (body: Pair -> unit) : unit =
    scratch (fun setup ->
        datedTarget setup
        SqliteConnection.ClearAllPools()

        let copy (name: string) : string * string =
            let target = Path.Combine(setup.Root, $"target-{name}")
            copyDirectory setup.Target target
            let config = Path.Combine(setup.Root, $"profiles-{name}.yml")
            File.WriteAllText(config, policyYaml target setup.Source policy)
            target, config

        let csTarget, csConfig = copy "cs"
        let fsTarget, fsConfig = copy "fs"

        body
            { Setup = setup
              CsTarget = csTarget
              FsTarget = fsTarget
              CsConfig = csConfig
              FsConfig = fsConfig })

/// One command, once per build, each against its own copy of the target.
let private both (pair: Pair) (parts: string list) : Run * Run =
    let withConfig (config: string) = parts @ [ "--config"; config ]
    start csharpDll (withConfig pair.CsConfig), start fsharpDll (withConfig pair.FsConfig)

let private query (target: string) (sql: string) : string list =
    SqliteConnection.ClearAllPools()
    use connection = new SqliteConnection(connectionString target)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    use reader = command.ExecuteReader()
    [ while reader.Read() do
          yield String.Join("|", [ for i in 0 .. reader.FieldCount - 1 -> string (reader.GetValue i) ]) ]

/// What a prune leaves behind, compared as rows: the snapshot ids, the rows that remain (by snapshot, path, content and
/// kind) and the blob names. Layout and timestamps are not part of it.
let private remaining (target: string) : string list * string list * string list =
    query target "SELECT id FROM snapshots ORDER BY id",
    query target "SELECT snapshot_id, relative_path, ifnull(content_hash, '~'), change_kind FROM file_versions ORDER BY id",
    (ContentStore.storedHashes (ContentStore.layout target) |> Seq.map Vara.Domain.ContentHash.value |> Seq.sort |> List.ofSeq)

let private headline (pattern: string) (run: Run) : string list =
    [ for found in Regex.Matches(run.Out, pattern) -> found.Value ]

module ``Prune command`` =
    [<Fact>]
    let ``prune leaves the same snapshots rows and blobs in both builds`` () =
        withPair true (fun pair ->
            let cs, fs = both pair [ "prune"; "docs"; "--yes" ]
            Assert.Equal(0, cs.ExitCode)
            Assert.Equal(cs.ExitCode, fs.ExitCode)

            let csRemaining = remaining pair.CsTarget
            let fsRemaining = remaining pair.FsTarget
            let ids, _, blobs = fsRemaining
            // The tiers decided something: two snapshots went, and their content with them.
            Assert.Equal<string list>([ "2"; "3"; "4"; "6" ], ids)
            Assert.Equal(4, blobs.Length)
            Assert.True((csRemaining = fsRemaining), $"C#: {csRemaining}\nF#: {fsRemaining}")

            // The same sentence in both, apart from the progress lines each prints.
            let sentence = @"Removed \d+ snapshot\(s\) and \d+ unreferenced content blob\(s\)\."
            Assert.Equal<string list>(headline sentence cs, headline sentence fs)
            Assert.NotEmpty(headline sentence fs))

    [<Fact>]
    let ``a second prune finds nothing more in either build`` () =
        withPair true (fun pair ->
            let cs, fs = both pair [ "prune"; "docs"; "--yes" ]
            Assert.Equal(cs.ExitCode, fs.ExitCode)
            let again = both pair [ "prune"; "docs"; "--yes" ]
            Assert.Equal(0, (fst again).ExitCode)
            Assert.Equal(0, (snd again).ExitCode)
            Assert.True((remaining pair.CsTarget = remaining pair.FsTarget)))

module ``No retention policy configured`` =
    [<Fact>]
    let ``prune without a configured policy is refused by both builds`` () =
        withPair false (fun pair ->
            let before = remaining pair.FsTarget
            let cs, fs = both pair [ "prune"; "docs"; "--yes" ]
            Assert.Equal(1, cs.ExitCode)
            Assert.Equal(cs.ExitCode, fs.ExitCode)
            Assert.Contains("no retention policy configured", cs.Err)
            Assert.Contains("no retention policy configured", fs.Err)
            // Nothing was deleted by either.
            Assert.True((remaining pair.CsTarget = before))
            Assert.True((remaining pair.FsTarget = before)))

/// The report of a check: the counts and each finding, which come after the progress lines (those differ in wording
/// and are left out). Line breaks and spaces are left out too, because a console 80 columns wide folds the C# lines.
let private findings (run: Run) : string =
    run.Out.Split('\n')
    |> Seq.map (fun line -> line.TrimEnd('\r'))
    |> Seq.skipWhile (fun line -> not (line.StartsWith "Checked " && line.Contains "blob(s):"))
    |> Seq.collect (fun line -> line |> Seq.filter (fun c -> not (Char.IsWhiteSpace c)) |> Seq.map string)
    |> String.concat ""

/// Deletes the blob of the first version, truncates the blob of the second one, in the target copy.
let private damage (target: string) : unit =
    let layout = ContentStore.layout target

    let byVersion =
        query target "SELECT content_hash FROM file_versions WHERE content_hash IS NOT NULL ORDER BY id"
        |> List.map (fun text -> (Vara.Domain.ContentHash.tryCreate text).Value)

    let first = ContentStore.blobPath layout byVersion[0]
    let second = ContentStore.blobPath layout byVersion[1]
    File.SetAttributes(first, FileAttributes.Normal)
    File.Delete first
    File.SetAttributes(second, FileAttributes.Normal)
    File.WriteAllText(second, "trunc")

module ``Check command verifies stored content against the manifest`` =
    [<Fact>]
    let ``check reports the same findings and exits the same in both builds`` () =
        withPair true (fun pair ->
            damage pair.CsTarget
            damage pair.FsTarget
            let cs, fs = both pair [ "check"; "docs" ]
            Assert.Equal(2, cs.ExitCode)
            Assert.Equal(cs.ExitCode, fs.ExitCode)
            // The text is compared without spaces, as it may be folded.
            Assert.Contains("1missing,1corrupt", findings fs)
            Assert.Equal(findings cs, findings fs)
            Assert.Contains("Missing:", fs.Out)
            Assert.Contains("Corrupt:", fs.Out))

    [<Fact>]
    let ``check of an intact target agrees`` () =
        withPair true (fun pair ->
            let cs, fs = both pair [ "check"; "docs" ]
            Assert.Equal(0, cs.ExitCode)
            Assert.Equal(cs.ExitCode, fs.ExitCode)
            Assert.Equal(findings cs, findings fs))

module ``Quick mode trades thoroughness for speed`` =
    [<Fact>]
    let ``quick check agrees on a target with a deleted and a truncated blob`` () =
        withPair true (fun pair ->
            damage pair.CsTarget
            damage pair.FsTarget
            let cs, fs = both pair [ "check"; "docs"; "--quick" ]
            // Only the deleted blob is found: the truncated one is present.
            Assert.Equal(2, cs.ExitCode)
            Assert.Equal(cs.ExitCode, fs.ExitCode)
            Assert.Equal(findings cs, findings fs)
            Assert.DoesNotContain("Corrupt:", fs.Out))

module ``Check exits with a scriptable status`` =
    [<Fact>]
    let ``a cancelled check keeps the C# build's partial-failure code`` () =
        // Cancellation cannot be delivered to a child process. What the processes do share is the code of a check that
        // found problems, which is not the code of a hard error such as a missing profile, in either build.
        withPair true (fun pair ->
            damage pair.CsTarget
            damage pair.FsTarget
            let unknown (config: string) (dll: string) = start dll [ "check"; "nothing"; "--config"; config ]
            Assert.Equal(1, (unknown pair.CsConfig csharpDll).ExitCode)
            Assert.Equal(1, (unknown pair.FsConfig fsharpDll).ExitCode))
