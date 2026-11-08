namespace Vara.Domain

/// Every way loading or resolving a profile can fail.
[<RequireQualifiedAccess>]
type ConfigError =
    | FileNotFound of configPath: string
    | Malformed of configPath: string * reason: string
    | InvalidProfile of profileName: string * reason: string
    | DuplicateName of profileName: string
    | UnknownProfile of profileName: string
    | ProfileNameRequired

module ConfigError =
    /// The message a command reports. Worded as the C# implementation words it.
    let render (error: ConfigError) : string =
        match error with
        | ConfigError.FileNotFound configPath -> $"Profile configuration file not found at '{configPath}'."
        | ConfigError.Malformed(configPath, reason) -> $"Profile configuration file '{configPath}' is malformed: {reason}"
        | ConfigError.InvalidProfile(name, reason) -> $"Profile '{name}' is invalid: {reason}"
        | ConfigError.DuplicateName name -> $"Profile name '{name}' is defined more than once in the configuration file."
        | ConfigError.UnknownProfile name -> $"No profile named '{name}' was found in the configuration file."
        | ConfigError.ProfileNameRequired ->
            "No profile name was given, and the current directory is not inside a profile's target root. "
            + "Specify a profile name."
