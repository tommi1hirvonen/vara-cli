/// The cases of the maintenance corpus: `prune` and `check` on a target that the F# backup wrote, with snapshots at
/// several times so that the retention tiers have something to decide. Observed as the snapshot ids and blob names that
/// remain after a prune, the findings and exit codes of a check, and the refusal for a missing policy. Rows and counts
/// are observed, not layout.
///
/// Not covered here, on purpose: the interactive flows (a child process cannot be driven through a prompt, so they are
/// tested in process with scripted prompts), and cancellation (a graceful Ctrl+C cannot be delivered to a child
/// process, so it rests on the engine tests and the in-process command tests).
module Vara.Cli.Tests.MaintenanceCorpus

open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.Data.Sqlite
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests
open Vara.Cli.Tests.ProcessCorpus
open Vara.Testing.Anonymize
open Vara.Engine

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

/// The dated target and its configuration, with a retention policy or without one.
let private withTarget (policy: bool) (body: Scratch -> string) : string =
    observing (fun setup ->
        datedTarget setup
        SqliteConnection.ClearAllPools()
        File.WriteAllText(setup.Config, policyYaml setup.Target setup.Source policy)
        body setup)

let private run (dll: string) (setup: Scratch) (parts: string list) : Run = start dll (parts @ [ "--config"; setup.Config ])

let private query (target: string) (sql: string) : string list =
    SqliteConnection.ClearAllPools()
    use connection = new SqliteConnection(connectionString target)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    use reader = command.ExecuteReader()

    [ while reader.Read() do
          yield String.Join("|", [ for i in 0 .. reader.FieldCount - 1 -> string (reader.GetValue i) ]) ]

/// What a prune leaves behind, as rows: the snapshot ids, the rows that remain (by snapshot, path, content and kind) and
/// the blob names. Layout and timestamps are not part of it.
let private remaining (setup: Scratch) : string list =
    let target = setup.Target

    [ yield! lines "snapshot ids" (query target "SELECT id FROM snapshots ORDER BY id")
      yield!
          lines
              "rows"
              (query target "SELECT snapshot_id, relative_path, ifnull(content_hash, '~'), change_kind FROM file_versions ORDER BY id"
               |> List.map (anonymize setup.Root))
      yield!
          lines
              "blobs"
              (ContentStore.storedHashes (ContentStore.layout target) |> Seq.map Vara.Domain.ContentHash.value |> Seq.sort |> List.ofSeq) ]

let private headline (pattern: string) (output: string) : string list =
    [ for found in Regex.Matches(output, pattern) -> found.Value ]

/// The report of a check: the counts and each finding, which come after the progress lines (those differ in wording
/// and are left out). Line breaks and spaces are left out too, because a console 80 columns wide folds the C# lines.
let private findings (setup: Scratch) (output: string) : string =
    let despaced (value: string) = String(value |> Seq.filter (fun c -> not (Char.IsWhiteSpace c)) |> Array.ofSeq)

    output.Split '\n'
    |> Seq.map _.TrimEnd('\r')
    |> Seq.skipWhile (fun line -> not (line.StartsWith "Checked " && line.Contains "blob(s):"))
    |> Seq.collect (fun line -> line |> Seq.filter (fun c -> not (Char.IsWhiteSpace c)) |> Seq.map string)
    |> String.concat ""
    |> anonymizeFlat despaced setup.Root

/// Deletes the blob of the first version and truncates the blob of the second one.
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

let private sentence = @"Removed \d+ snapshot\(s\) and \d+ unreferenced content blob\(s\)\."

let private prune (dll: string) : string =
    withTarget true (fun setup ->
        let first = run dll setup [ "prune"; "docs"; "--yes" ]
        let state = remaining setup
        let again = run dll setup [ "prune"; "docs"; "--yes" ]

        text
            [ line "prune exit" (string first.ExitCode)
              yield! lines "prune said" (headline sentence first.Out)
              yield! state
              line "second prune exit" (string again.ExitCode)
              yield! lines "second prune said" (headline sentence again.Out)
              yield! remaining setup ])

let private noPolicy (dll: string) : string =
    withTarget false (fun setup ->
        let before = remaining setup
        let refused = run dll setup [ "prune"; "docs"; "--yes" ]

        text
            [ line "exit" (string refused.ExitCode)
              line "names the missing policy" (string (refused.Err.Contains "no retention policy configured"))
              line "deleted nothing" (string (remaining setup = before)) ])

let private checkDamaged (dll: string) : string =
    withTarget true (fun setup ->
        damage setup.Target
        let full = run dll setup [ "check"; "docs" ]

        text
            [ line "exit" (string full.ExitCode)
              line "findings" (findings setup full.Out)
              line "lists missing" (string (full.Out.Contains "Missing:"))
              line "lists corrupt" (string (full.Out.Contains "Corrupt:")) ])

let private checkIntact (dll: string) : string =
    withTarget true (fun setup ->
        let full = run dll setup [ "check"; "docs" ]
        text [ line "exit" (string full.ExitCode); line "findings" (findings setup full.Out) ])

let private checkQuick (dll: string) : string =
    withTarget true (fun setup ->
        damage setup.Target
        // Only the deleted blob is found: the truncated one is present.
        let quick = run dll setup [ "check"; "docs"; "--quick" ]

        text
            [ line "exit" (string quick.ExitCode)
              line "findings" (findings setup quick.Out)
              line "lists corrupt" (string (quick.Out.Contains "Corrupt:")) ])

let private checkUnknown (dll: string) : string =
    // Cancellation cannot be delivered to a child process. What the processes do share is the code of a check that
    // found problems, which is not the code of a hard error such as a missing profile.
    withTarget true (fun setup ->
        damage setup.Target
        text [ line "unknown profile exit" (string (run dll setup [ "check"; "nothing" ]).ExitCode) ])

let cases: Case list =
    [ { Name = "prune"; Observe = prune }
      { Name = "prune-without-policy"; Observe = noPolicy }
      { Name = "check-damaged"; Observe = checkDamaged }
      { Name = "check-intact"; Observe = checkIntact }
      { Name = "check-quick"; Observe = checkQuick }
      { Name = "check-unknown-profile"; Observe = checkUnknown } ]
