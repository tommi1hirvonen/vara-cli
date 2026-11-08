namespace Vara.Domain

/// A retention count: zero or more snapshots to keep.
type RetentionCount = private RetentionCount of int

module RetentionCount =
    let tryCreate (count: int) : RetentionCount option =
        if count >= 0 then Some(RetentionCount count) else None

    let zero: RetentionCount = RetentionCount 0
    let value (RetentionCount count) : int = count

/// A concurrency limit: one or more.
type ConcurrencyLimit = private ConcurrencyLimit of int

module ConcurrencyLimit =
    let tryCreate (limit: int) : ConcurrencyLimit option =
        if limit > 0 then Some(ConcurrencyLimit limit) else None

    let value (ConcurrencyLimit limit) : int = limit

type RetentionPolicy =
    { KeepDaily: RetentionCount
      KeepWeekly: RetentionCount
      KeepMonthly: RetentionCount
      KeepYearly: RetentionCount }

/// A setting left out means the engine uses its own default.
type Concurrency =
    { Scan: ConcurrencyLimit option
      Transfer: ConcurrencyLimit option }

type Source =
    { Path: AbsolutePath
      Recursive: bool
      Excludes: string list
      IncludeGlobs: string list
      ExcludeGlobs: string list }

/// Why a set of sources and a target cannot form a profile.
[<RequireQualifiedAccess>]
type ProfileViolation =
    | NoSources
    | TargetOverlapsSource of target: AbsolutePath * source: AbsolutePath
    | SourcesOverlap of first: AbsolutePath * second: AbsolutePath

/// A backup profile. Only Profile.create produces one: it has at least one source, no two sources overlap,
/// and the target overlaps none of them.
type Profile =
    private
        { Name: string
          Target: AbsolutePath
          Sources: Source list
          Retention: RetentionPolicy option
          Concurrency: Concurrency option }

module Profile =
    let private firstOverlappingPair (sources: Source list) : (Source * Source) option =
        sources
        |> List.indexed
        |> List.tryPick (fun (index, first) ->
            sources
            |> List.skip (index + 1)
            |> List.tryFind (fun second -> AbsolutePath.overlaps first.Path second.Path)
            |> Option.map (fun second -> first, second))

    let create
        (name: string)
        (target: AbsolutePath)
        (sources: Source list)
        (retention: RetentionPolicy option)
        (concurrency: Concurrency option)
        : Result<Profile, ProfileViolation> =
        match sources with
        | [] -> Error ProfileViolation.NoSources
        | _ ->
            match sources |> List.tryFind (fun source -> AbsolutePath.overlaps target source.Path) with
            | Some source -> Error(ProfileViolation.TargetOverlapsSource(target, source.Path))
            | None ->
                match firstOverlappingPair sources with
                | Some(first, second) -> Error(ProfileViolation.SourcesOverlap(first.Path, second.Path))
                | None ->
                    Ok
                        { Name = name
                          Target = target
                          Sources = sources
                          Retention = retention
                          Concurrency = concurrency }

    let name (profile: Profile) : string = profile.Name
    let target (profile: Profile) : AbsolutePath = profile.Target
    let sources (profile: Profile) : Source list = profile.Sources
    let retention (profile: Profile) : RetentionPolicy option = profile.Retention
    let concurrency (profile: Profile) : Concurrency option = profile.Concurrency

/// A target root found from the working directory; it has no sources.
type DiscoveredRoot = { Name: string; Root: AbsolutePath }

/// What profile resolution produces. Commands that need sources accept only Configured.
[<RequireQualifiedAccess>]
type Resolution =
    | Configured of Profile
    | Discovered of DiscoveredRoot
