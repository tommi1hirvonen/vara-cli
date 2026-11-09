/// Turns the neutral configuration tree into profiles. Each field validator is a function
/// from a node to Result; the first Error stops the whole validation.
module Vara.Domain.ProfileValidation

open System

type private Fields = (string * ConfigNode) list

let private invalid (profile: string) (reason: string) : Result<'T, ConfigError> =
    Error(ConfigError.InvalidProfile(profile, reason))

let private find (key: string) (fields: Fields) : ConfigNode option = ConfigNode.tryField key fields

let private scalar (key: string) (fields: Fields) : string option =
    match find key fields with
    | Some(ConfigNode.Scalar text) -> Some text
    | Some(ConfigNode.Sequence _ | ConfigNode.Mapping _ | ConfigNode.Unsupported)
    | None -> None

let private nonBlank (text: string option) : string option =
    text |> Option.filter (fun text -> not (String.IsNullOrWhiteSpace text))

let private mappingOf (profile: string) (reason: string) (node: ConfigNode) : Result<Fields, ConfigError> =
    match node with
    | ConfigNode.Mapping fields -> Ok fields
    | ConfigNode.Scalar _
    | ConfigNode.Sequence _
    | ConfigNode.Unsupported -> invalid profile reason

let private stringList (profile: string) (sourcePath: string) (key: string) (fields: Fields) : Result<string list, ConfigError> =
    match find key fields with
    | None -> Ok []
    | Some(ConfigNode.Sequence entries) ->
        entries
        |> Result.traverse (fun entry ->
            match entry with
            | ConfigNode.Scalar text -> Ok text
            | other ->
                invalid
                    profile
                    $"source '{sourcePath}' field '{key}' contains an entry that is {ConfigNode.describe other} instead of a string")
    | Some other ->
        invalid profile $"source '{sourcePath}' field '{key}' must be a list, but found {ConfigNode.describe other}"

let private recursiveFlag (profile: string) (sourcePath: string) (fields: Fields) : Result<bool, ConfigError> =
    match scalar "recursive" fields with
    | None -> Ok true
    | Some text ->
        match Boolean.TryParse text with
        | true, flag -> Ok flag
        | _ -> invalid profile $"source '{sourcePath}' has an invalid 'recursive' value '{text}' (expected true/false)"

let private source (profile: string) (fields: Fields) : Result<Source, ConfigError> =
    result {
        let! path =
            nonBlank (scalar "path" fields)
            |> Result.ofOption (ConfigError.InvalidProfile(profile, "each source must define a 'path'"))

        let! recursive = recursiveFlag profile path fields
        let! excludes = stringList profile path "exclude" fields
        let! includeGlobs = stringList profile path "include_globs" fields
        let! excludeGlobs = stringList profile path "exclude_globs" fields

        let! absolute =
            AbsolutePath.tryCreate path
            |> Result.ofOption (
                ConfigError.InvalidProfile(profile, $"Source path '{path}' must be a fully-qualified, absolute path.")
            )

        return
            { Path = absolute
              Recursive = recursive
              Excludes = excludes
              IncludeGlobs = includeGlobs
              ExcludeGlobs = excludeGlobs }
    }

/// An absent field is Ok None; a present one must be a whole number that `accept` takes.
let private wholeNumber
    (profile: string)
    (section: string)
    (expected: string)
    (accept: int -> 'T option)
    (key: string)
    (fields: Fields)
    : Result<'T option, ConfigError> =
    match scalar key fields with
    | None -> Ok None
    | Some text ->
        let parsed =
            match Int32.TryParse(text: string) with
            | true, number -> accept number
            | _ -> None

        match parsed with
        | Some value -> Ok(Some value)
        | None -> invalid profile $"{section} field '{key}' has an invalid value '{text}' (expected {expected})"

let private retention (profile: string) (fields: Fields) : Result<RetentionPolicy option, ConfigError> =
    match find "retention" fields with
    | None -> Ok None
    | Some node ->
        result {
            let! section = mappingOf profile "'retention' must be a mapping" node

            let keep key =
                wholeNumber profile "retention" "a non-negative whole number" RetentionCount.tryCreate key section
                |> Result.map (Option.defaultValue RetentionCount.zero)

            let! daily = keep "keep_daily"
            let! weekly = keep "keep_weekly"
            let! monthly = keep "keep_monthly"
            let! yearly = keep "keep_yearly"

            return
                Some
                    { KeepDaily = daily
                      KeepWeekly = weekly
                      KeepMonthly = monthly
                      KeepYearly = yearly }
        }

let private concurrency (profile: string) (fields: Fields) : Result<Concurrency option, ConfigError> =
    match find "concurrency" fields with
    | None -> Ok None
    | Some node ->
        result {
            let! section = mappingOf profile "'concurrency' must be a mapping" node

            let limit key =
                wholeNumber profile "concurrency" "a positive whole number" ConcurrencyLimit.tryCreate key section

            let! scan = limit "scan_concurrency"
            let! transfer = limit "transfer_concurrency"
            return Some { Scan = scan; Transfer = transfer }
        }

let private violationReason (violation: ProfileViolation) : string =
    match violation with
    | ProfileViolation.NoSources -> "A profile must define at least one source."
    | ProfileViolation.TargetOverlapsSource(target, source) ->
        $"Target root '{AbsolutePath.value target}' overlaps source path '{AbsolutePath.value source}'. "
        + "A profile's target must not be the same as, contain, or be contained by any of its own sources."
    | ProfileViolation.SourcesOverlap(first, second) ->
        $"Source path '{AbsolutePath.value first}' overlaps source path '{AbsolutePath.value second}'. "
        + "A profile's sources must not be the same as, contain, or be contained by any of its own other sources."

let private profile (entry: ConfigNode) : Result<Profile, ConfigError> =
    result {
        let! fields = mappingOf "<unnamed>" "each entry under 'profiles' must be a mapping" entry

        let! name =
            match nonBlank (scalar "name" fields) with
            | Some name -> Ok name
            | None -> invalid (defaultArg (scalar "name" fields) "<unnamed>") "missing required field 'name'"

        let! target =
            nonBlank (scalar "target" fields)
            |> Result.ofOption (ConfigError.InvalidProfile(name, "missing required field 'target'"))

        let! sourceEntries =
            match find "sources" fields with
            | Some(ConfigNode.Sequence(_ :: _ as entries)) -> Ok entries
            | _ -> invalid name "must define at least one source under 'sources'"

        let! sources =
            sourceEntries
            |> Result.traverse (fun entry ->
                mappingOf name "each entry under 'sources' must be a mapping" entry
                |> Result.bind (source name))

        let! retention = retention name fields
        let! concurrency = concurrency name fields

        let! targetPath =
            AbsolutePath.tryCreate target
            |> Result.ofOption (
                ConfigError.InvalidProfile(name, $"Target root '{target}' must be a fully-qualified, absolute path.")
            )

        return!
            Profile.create name targetPath sources retention concurrency
            |> Result.mapError (fun violation -> ConfigError.InvalidProfile(name, violationReason violation))
    }

let private addUnique (accepted: Profile list) (entry: ConfigNode) : Result<Profile list, ConfigError> =
    result {
        let! next = profile entry
        let name = Profile.name next

        let isTaken =
            accepted |> List.exists (fun other -> String.Equals(Profile.name other, name, StringComparison.OrdinalIgnoreCase))

        if isTaken then
            return! Error(ConfigError.DuplicateName name)
        else
            return next :: accepted
    }

/// Validates the entries under `profiles`, in order, stopping at the first error.
let validateProfiles (entries: ConfigNode list) : Result<Profile list, ConfigError> =
    entries
    |> List.fold (fun accepted entry -> accepted |> Result.bind (fun profiles -> addUnique profiles entry)) (Ok [])
    |> Result.map List.rev
