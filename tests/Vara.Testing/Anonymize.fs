/// Takes out of an observed text what differs between machines and runs: the scratch directory, in the forms a program
/// prints a path.
module Vara.Testing.Anonymize

open System

/// The placeholder for the scratch directory of a run, which differs from one run to the next.
let rootPlaceholder = "<root>"

[<System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetLongPathNameW")>]
extern uint32 private getLongPathName(string shortPath, System.Text.StringBuilder longPath, uint32 size)

/// The path with its 8.3 names expanded: the temporary directory may be given in the short form, and the commands print
/// the long one.
let longPath (path: string) : string =
    let buffer = System.Text.StringBuilder 1024
    let length = getLongPathName (path, buffer, uint32 buffer.Capacity)
    if length = 0u || int length > buffer.Capacity then path else buffer.ToString()

/// The ways a scratch directory is written in output: as a path in its short and long forms, escaped in JSON, and as a
/// mirror path (no colon). The longest comes first, so that a shorter spelling never cuts into a longer one.
let spellings (root: string) : string list =
    [ root; longPath root ]
    |> List.collect (fun path ->
        let mirror = path.Replace(":", "")
        [ path; path.Replace(@"\", @"\\"); mirror; mirror.Replace(@"\", @"\\") ])
    |> List.distinct
    |> List.sortByDescending _.Length

/// The text with the scratch directory taken out, so that two runs in two directories read the same.
let anonymize (root: string) (text: string) : string =
    spellings root
    |> List.fold (fun (text: string) (spelling: string) -> text.Replace(spelling, rootPlaceholder, StringComparison.OrdinalIgnoreCase)) text

