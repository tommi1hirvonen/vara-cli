/// Finds the profile a command works on: by name from the configuration file, or from the working directory.
module Vara.Engine.ProfileResolution

open System
open System.IO
open Vara.Domain

let private manifestPath = Path.Combine(".vara", "profile.db")

/// The profile with that name; letter case is ignored.
let byName (name: string) (profiles: Profile list) : Result<Profile, ConfigError> =
    profiles
    |> List.tryFind (fun profile -> String.Equals(Profile.name profile, name, StringComparison.OrdinalIgnoreCase))
    |> Result.ofOption (ConfigError.UnknownProfile name)

let forProfile (configPath: string) (name: string) : Result<Profile, ConfigError> =
    ConfigFile.load configPath |> Result.bind (byName name)

/// The nearest directory at or above the start that holds a `.vara\profile.db`.
let findTargetRoot (manifestExists: string -> bool) (startDirectory: string) : string option =
    let start = Path.GetFullPath startDirectory

    Seq.unfold
        (fun directory ->
            directory
            |> Option.map (fun current ->
                current, Directory.GetParent current |> Option.ofObj |> Option.map (fun parent -> parent.FullName)))
        (Some start)
    |> Seq.tryFind (fun directory -> manifestExists (Path.Combine(directory, manifestPath)))

let fromDirectory (startDirectory: string) : Result<DiscoveredRoot, ConfigError> =
    findTargetRoot File.Exists startDirectory
    |> Option.bind (fun directory ->
        let name =
            match Path.GetFileName directory with
            | "" -> directory
            | folder -> folder

        AbsolutePath.tryCreate directory |> Option.map (fun root -> { Name = name; Root = root }))
    |> Result.ofOption ConfigError.ProfileNameRequired

/// For commands that browse or restore history. A given name is resolved from the configuration file and the
/// working directory is not consulted; without a name, the working directory decides and the file is not read.
let forBrowsing (configPath: string) (name: string option) (startDirectory: string) : Result<Resolution, ConfigError> =
    match name |> Option.filter (fun name -> not (String.IsNullOrWhiteSpace name)) with
    | Some name -> forProfile configPath name |> Result.map Resolution.Configured
    | None -> fromDirectory startDirectory |> Result.map Resolution.Discovered
