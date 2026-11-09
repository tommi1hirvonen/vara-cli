/// The cases of the `vara backup` corpus: what a script sees of a real run, a dry run, a run with a failed path and a
/// refused run, each on a fresh source and target.
module Vara.Cli.Tests.BackupCorpus

open System.IO
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.ProcessCorpus
open Vara.Testing.Anonymize

/// A `vara backup docs` run for the profile of the scratch directory.
let private backup (dll: string) (setup: Scratch) (arguments: string list) : Run =
    start dll ([ "backup"; "docs"; "--config"; setup.Config ] @ arguments)

/// The JSON run and then the plain-text run of one command, on the same target (the second run sees the first's work).
let private jsonThenText (dll: string) (setup: Scratch) (arguments: string list) : string =
    let json = backup dll setup (arguments @ [ "--json" ])
    let plain = backup dll setup arguments

    text
        [ line "json exit" (string json.ExitCode)
          line "json" (anonymize setup.Root (normalizedJson json.Out))
          line "text exit" (string plain.ExitCode)
          yield! lines "text counts" (counts plain.Out) ]

let private realRun (dll: string) : string =
    observing (fun setup ->
        write setup "a.txt" "alpha"
        write setup "sub/b.txt" "beta beta"
        jsonThenText dll setup [])

let private dryRun (dll: string) : string =
    observing (fun setup ->
        write setup "a.txt" "alpha"
        write setup "sub/b.txt" "beta beta"
        jsonThenText dll setup [ "--dry-run" ])

let private failedPath (dll: string) : string =
    observing (fun setup ->
        write setup "locked.txt" "locked"
        write setup "fine.txt" "fine"
        use _hold = new FileStream(Path.Combine(setup.Source, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None)
        jsonThenText dll setup [])

let private refused (dll: string) : string =
    observing (fun setup ->
        write setup "a.txt" "alpha"
        let target = Vara.Domain.AbsolutePath.tryCreate setup.Target |> Option.get
        use _lock = Vara.Engine.RunLock.tryAcquire "other" target |> Result.defaultWith (fun _ -> failwith "no lock")
        let run = backup dll setup [ "--json" ]

        text
            [ line "exit" (string run.ExitCode)
              line "names the busy target" (string (run.Err.Contains "Another operation is already using target")) ])

let cases: Case list =
    [ { Name = "real-run"; Observe = realRun }
      { Name = "dry-run"; Observe = dryRun }
      { Name = "failed-path"; Observe = failedPath }
      { Name = "refused-run"; Observe = refused } ]
