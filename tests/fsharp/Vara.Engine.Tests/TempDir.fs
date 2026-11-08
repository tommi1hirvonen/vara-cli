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

let write (path: string) (text: string) : unit =
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    File.WriteAllText(path, text)
