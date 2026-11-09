/// The F# command line against the recorded observations of the C# one. Each case runs the F# `vara.dll` as a process on
/// the same fixture that the C# build was recorded on, and its observation (exit codes, normalized JSON, projected rows
/// and counts, the files and blobs a command leaves) is compared with the recorded text.
///
/// The cases are in `BackupCorpus`, `HistoryCorpus` and `MaintenanceCorpus`; the recorded files are in the `Corpus` folder,
/// one per case.
module Vara.Cli.Tests.ProcessCorpusTests

open System.IO
open Xunit
open Vara.Cli.Tests.ProcessCorpus
open Vara.Testing

let private compareArea (area: string) (cases: Case list) : unit =
    let compared =
        cases |> List.map (fun case -> case.Name, Corpus.read area (case.Name + ".txt"), case.Observe fsharpDll)

    Corpus.assertSame compared

/// A recorded file for every case, and no recorded file without a case.
let private assertRecorded (area: string) (cases: Case list) : unit =
    let recorded = Corpus.names area |> List.map Path.GetFileNameWithoutExtension |> List.sort
    Assert.Equal<string list>(cases |> List.map (fun case -> case.Name) |> List.sort, recorded)

[<Fact>]
let ``the built F# command exists`` () = Assert.True(File.Exists fsharpDll)

module ``Backup command corpus`` =
    [<Fact>]
    let ``every backup case is recorded`` () = assertRecorded "backup" BackupCorpus.cases

    [<Fact>]
    let ``the F# backup gives the recorded JSON, exit codes and counts`` () = compareArea "backup" BackupCorpus.cases

module ``History command corpus`` =
    [<Fact>]
    let ``every history case is recorded`` () = assertRecorded "history" HistoryCorpus.cases

    [<Fact>]
    let ``the F# history commands give the recorded rows, files and exit codes`` () = compareArea "history" HistoryCorpus.cases

module ``Maintenance command corpus`` =
    [<Fact>]
    let ``every maintenance case is recorded`` () = assertRecorded "maintenance" MaintenanceCorpus.cases

    [<Fact>]
    let ``the F# prune and check leave and report the recorded state`` () = compareArea "maintenance" MaintenanceCorpus.cases
