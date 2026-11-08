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
