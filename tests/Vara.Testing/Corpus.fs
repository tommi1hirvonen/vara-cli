/// A recorded corpus: reference results stored once as text files and compared with the results of the code under test.
/// The files are data. A comparison is a plain function of the recorded text and the actual result.
module Vara.Testing.Corpus

open System
open System.IO

let private normalize (text: string) : string = text.Replace("\r\n", "\n")

/// The folder of one corpus area, as copied next to the test binaries.
let folder (area: string) : string = Path.Combine(AppContext.BaseDirectory, "Corpus", area)

/// A line that starts with this marker is a note in a recorded file and not part of the recorded text. A recorded
/// result that was changed on purpose carries a note that gives the reason.
let notePrefix = "#!"

let private withoutNotes (text: string) : string =
    text.Split '\n' |> Array.filter (fun line -> not (line.StartsWith notePrefix)) |> String.concat "\n"

/// The recorded text of a case, with line endings normalized so that a checkout's autocrlf cannot change a comparison.
let read (area: string) (name: string) : string =
    withoutNotes (normalize (File.ReadAllText(Path.Combine(folder area, name))))

/// The recorded input of a case, as written: no notes are taken out.
let readRaw (area: string) (name: string) : string =
    normalize (File.ReadAllText(Path.Combine(folder area, name)))

/// The file names of an area (without the folder), in ordinal order. An area without files has no names.
let names (area: string) : string list =
    if Directory.Exists(folder area) then
        Directory.EnumerateFiles(folder area) |> Seq.map Path.GetFileName |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofSeq
    else
        []

/// A comparison failure that shows both texts, since a bare equality failure on a long text is hard to read.
let describeMismatch (name: string) (recorded: string) (actual: string) : string =
    $"{name}:\n--- recorded ---\n{recorded}\n--- actual ---\n{actual}"

/// `None` when the actual result equals the recorded text, otherwise the description of the difference.
let compare (name: string) (recorded: string) (actual: string) : string option =
    let comparable (text: string) = (normalize text).TrimEnd '\n'

    if String.Equals(comparable recorded, comparable actual, StringComparison.Ordinal) then
        None
    else
        Some(describeMismatch name (comparable recorded) (comparable actual))

/// The case names that differ, for one assertion over a whole area.
let differences (cases: (string * string * string) list) : string list =
    cases |> List.choose (fun (name, recorded, actual) -> compare name recorded actual)

/// Fails with the whole description of every difference, which an equality assertion on a list would cut short.
let assertSame (cases: (string * string * string) list) : unit =
    match differences cases with
    | [] -> ()
    | found -> Xunit.Assert.Fail(String.concat "\n\n" found)
