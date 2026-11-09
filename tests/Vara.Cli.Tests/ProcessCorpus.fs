/// What a script can see of the command line: exit codes, normalized JSON, projected rows and counts, and the files a
/// command leaves. A case is a name and a function from the path of a `vara.dll` to the text that it observed. The
/// recorded text of a case is what the C# build gave on the same fixture; the F# test compares it with the F# build's.
module Vara.Cli.Tests.ProcessCorpus

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Vara.Cli.Tests.BackupCommandTests

/// A case of the process corpus: the file name it is recorded under (without the extension) and its observation.
type Case =
    { Name: string
      Observe: string -> string }

type Run =
    { ExitCode: int
      Out: string
      Err: string }

let fsharpDll: string = Path.Combine(AppContext.BaseDirectory, "vara.dll")

let start (dll: string) (arguments: string list) : Run =
    let info = ProcessStartInfo("dotnet")
    info.ArgumentList.Add dll

    for argument in arguments do
        info.ArgumentList.Add argument

    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use proc = Process.Start info |> nonNull
    let err = proc.StandardError.ReadToEndAsync()
    let out = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit()
    { ExitCode = proc.ExitCode; Out = out; Err = err.GetAwaiter().GetResult() }

/// The JSON object without the members that differ between two runs.
let normalizedJson (json: string) : string =
    let node = JsonNode.Parse(json) |> nonNull
    let obj = node.AsObject()
    obj.Remove "startedAt" |> ignore
    obj.Remove "completedAt" |> ignore
    obj.ToJsonString()

/// The count lines of a plain-text summary.
let counts (text: string) : string list =
    Regex.Matches(text, @"^  (Added|Changed|Moved|Deleted|To transfer|Transferred):\s+(.+?)\s*$", RegexOptions.Multiline)
    |> Seq.map (fun found -> $"{found.Groups[1].Value}={found.Groups[2].Value}")
    |> List.ofSeq

/// Observations are lines: a label and a value, so that a difference names what differed.
let line (label: string) (value: string) : string = $"{label}: {value}"

let lines (label: string) (values: string list) : string list =
    line label (string values.Length) :: (values |> List.map (fun value -> "  " + value))

let text (parts: string list) : string = String.concat "\n" parts

/// Runs a body in a fresh scratch directory and returns the text it observed.
let observing (body: Scratch -> string) : string =
    let result = ref ""
    scratch (fun setup -> result.Value <- body setup)
    result.Value
