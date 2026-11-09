/// Where the profile configuration lives, and loading it: locate, read, parse to the neutral tree, validate.
module Vara.Engine.ConfigFile

open System
open System.IO
open Vara.Domain

/// `~/.vara/profiles.yml`.
let defaultPath () : string =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".vara", "profiles.yml")

/// The alternate path when one is given, otherwise the default location.
let locate (alternatePath: string option) : string =
    alternatePath |> Option.defaultWith defaultPath

let load (configPath: string) : Result<Profile list, ConfigError> =
    if File.Exists configPath then
        File.ReadAllText configPath
        |> YamlConfig.parse configPath
        |> Result.bind ProfileValidation.validateProfiles
    else
        Error(ConfigError.FileNotFound configPath)

/// The file operations of a durable write, as functions, so that a test can record them or make one fail.
type DurableFiles =
    { CreateDirectory: string -> unit
      /// Writes the text to a new file, replacing one that is there.
      WriteText: string -> string -> unit
      /// Commits the file's bytes to durable storage, not only to the operating system's cache.
      Commit: string -> unit
      Exists: string -> bool
      /// `Replace temporary target`: the target's contents are the temporary file's, atomically.
      Replace: string -> string -> unit
      /// `Move temporary target` for a target that does not exist yet.
      Move: string -> string -> unit
      Delete: string -> unit }

module DurableFiles =
    let real: DurableFiles =
        { CreateDirectory = fun directory -> Directory.CreateDirectory directory |> ignore
          WriteText =
            fun path text ->
                use stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)
                use writer = new StreamWriter(stream, Text.UTF8Encoding false)
                writer.Write text
          Commit =
            fun path ->
                // A flush forces the bytes onto the disk. The writer above only emptied .NET's buffer into the
                // operating system's cache, which an abrupt loss of the machine would lose.
                use stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)
                stream.Flush true
          Exists = File.Exists
          Replace = fun temporary target -> File.Replace(temporary, target, null)
          Move = fun temporary target -> File.Move(temporary, target)
          Delete = File.Delete }

/// Writes the text to the configuration file so that the file is always either what it was or what the text says:
/// create the directory, write a temporary file beside the target (the same volume, so the swap is atomic), commit it
/// to disk, then replace the target with it, or move it there when there is no target yet. The temporary file is
/// removed whatever happens. An I/O failure is a value, with the file and the reason; any other exception leaves with
/// its stack.
let writeDurablyWith (files: DurableFiles) (configPath: string) (text: string) : Result<unit, ConfigWriteError> =
    let directory =
        match Path.GetDirectoryName configPath with
        | null
        | "" -> Environment.CurrentDirectory
        | directory -> directory

    let temporary = Path.Combine(directory, $".{Path.GetFileName configPath}.{Guid.NewGuid():N}.tmp")

    try
        try
            files.CreateDirectory directory
            files.WriteText temporary text
            files.Commit temporary

            if files.Exists configPath then
                files.Replace temporary configPath
            else
                files.Move temporary configPath

            Ok()
        finally
            if files.Exists temporary then
                files.Delete temporary
    with
    | :? IOException as error -> Error { ConfigPath = configPath; Reason = error.Message }
    | :? UnauthorizedAccessException as error -> Error { ConfigPath = configPath; Reason = error.Message }

let writeDurably: string -> string -> Result<unit, ConfigWriteError> = writeDurablyWith DurableFiles.real

/// Saves the profiles as the whole configuration file, written durably.
let save (configPath: string) (profiles: Profile list) : Result<unit, ConfigWriteError> =
    profiles |> ProfileNodes.ofProfiles |> YamlConfig.render |> writeDurably configPath
