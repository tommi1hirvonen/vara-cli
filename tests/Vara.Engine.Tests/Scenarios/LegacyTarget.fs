/// A target recorded as files, for the engine to continue. The recorded `vara` folder is the target's `.vara` (the
/// manifest and the blobs), the recorded `mirror` folder is what the mirror held under the source directory, and the
/// manifest's paths hold `@ROOT@` where the recording's scratch directory was. Users keep their targets across releases,
/// so the engine must read a target written earlier as it is.
module Vara.Engine.Tests.LegacyTarget

open System.IO
open Microsoft.Data.Sqlite
open Vara.Domain
open Vara.Engine.Tests.EngineScenarios
open Vara.Testing

let area = "engine-legacy"

/// The placeholder for the scratch directory in the recorded manifest.
let rootPlaceholder = "@ROOT@"

/// The two files of the recorded target, in the source.
let legacySources (rig: Rig) : unit =
    write rig "a.txt" "alpha" |> ignore
    write rig "sub\\b.txt" "beta" |> ignore

/// The part of a file's mirror path before `\source\<name>`: where the mirror keeps this rig's directory.
let mirrorPrefix (rig: Rig) : string =
    let key = ScanRules.toMirrorPath (Path.Combine(rig.Source, "a.txt")) |> ValueOption.get |> MirrorPath.value
    key.Substring(0, key.Length - @"\source\a.txt".Length)

let private copyTree (from: string) (into: string) : unit =
    for file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories) do
        let destination = Path.Combine(into, Path.GetRelativePath(from, file))
        Directory.CreateDirectory(Path.GetDirectoryName destination |> nonNull) |> ignore
        File.Copy(file, destination)
        File.SetAttributes(destination, FileAttributes.Normal)

/// Puts the recorded target in the rig's target, with the manifest's paths pointing at the rig's source.
let restore (rig: Rig) : unit =
    let folder = Corpus.folder area
    copyTree (Path.Combine(folder, "vara")) (Path.Combine(rig.Target, ".vara"))
    copyTree (Path.Combine(folder, "mirror")) (Path.Combine(rig.Target, mirrorPrefix rig))
    let database = Path.Combine(rig.Target, ".vara", "profile.db")
    use connection = new SqliteConnection $"Data Source={database};Pooling=False"
    connection.Open()
    use command = connection.CreateCommand()

    command.CommandText <-
        "UPDATE file_versions SET relative_path = REPLACE(relative_path, @placeholder, @prefix), previous_relative_path = REPLACE(previous_relative_path, @placeholder, @prefix)"

    command.Parameters.AddWithValue("@placeholder", rootPlaceholder) |> ignore
    command.Parameters.AddWithValue("@prefix", mirrorPrefix rig) |> ignore
    command.ExecuteNonQuery() |> ignore
