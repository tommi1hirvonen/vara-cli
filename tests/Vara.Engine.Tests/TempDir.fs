/// A scratch directory per test, removed afterwards (junctions first, so deletion never descends through one).
module Vara.Engine.Tests.TempDir

open System
open System.IO

let private removeJunctions (root: string) =
    for entry in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories) |> Seq.toArray do
        if Directory.Exists entry && (File.GetAttributes entry).HasFlag FileAttributes.ReparsePoint then
            Directory.Delete(entry, false)

/// Runs the body with a fresh empty directory, and removes it afterwards even when the body fails.
let using (body: string -> 'T) : 'T =
    let root = Path.Combine(Path.GetTempPath(), $"vara-fs-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory root |> ignore

    try
        body root
    finally
        // A repository of either engine may leave its connection pooled, which keeps the database file open.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

        if Directory.Exists root then
            removeJunctions root

            for file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) |> Seq.toArray do
                File.SetAttributes(file, FileAttributes.Normal)

            Directory.Delete(root, true)

let private runSubst (arguments: string) : int * string =
    let info = Diagnostics.ProcessStartInfo("cmd.exe", $"/c subst {arguments}")
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use proc = Diagnostics.Process.Start info |> nonNull
    let error = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, error

/// Maps a free drive letter to `target` for the body. Drive letters are machine-wide, so choosing a free letter and then
/// taking it races with every other test thread and every other test process; a named mutex serializes all users of the
/// mapping across processes.
let withSubst (target: string) (body: char -> 'T) : 'T =
    use gate = new Threading.Mutex(false, @"Global\vara-tests-subst")

    try
        gate.WaitOne() |> ignore
    with :? Threading.AbandonedMutexException ->
        () // A crashed holder; its mapping, if any, is one of the letters the search below skips.

    try
        let used = DriveInfo.GetDrives() |> Array.map (fun d -> Char.ToUpperInvariant d.Name[0]) |> Set.ofArray

        let letter =
            [ for code in int 'X' .. -1 .. int 'D' -> char code ] |> List.find (fun c -> not (used.Contains c))

        let code, error = runSubst $"{letter}: \"{target}\""
        if code <> 0 then failwith $"subst {letter}: failed: {error}"

        try
            body letter
        finally
            runSubst $"{letter}: /d" |> ignore
    finally
        gate.ReleaseMutex()

let write (path: string) (text: string) : unit =
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    File.WriteAllText(path, text)
