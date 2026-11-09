/// The configuration tree of saved profiles: the same shape that `YamlConfig.parse` produces, so that what is written
/// is read back by the loader as the same profiles (design D3).
module Vara.Domain.ProfileNodes

open System.Globalization

let private count (value: int) : ConfigNode =
    ConfigNode.Scalar(value.ToString(CultureInfo.InvariantCulture))

/// A list is written only when it has entries; the loader reads an absent list as empty.
let private listField (key: string) (values: string list) : (string * ConfigNode) list =
    match values with
    | [] -> []
    | _ -> [ (key, ConfigNode.Sequence(values |> List.map ConfigNode.Scalar)) ]

let private ofSource (source: Source) : ConfigNode =
    ConfigNode.Mapping(
        [ "path", ConfigNode.Scalar(AbsolutePath.value source.Path)
          "recursive", ConfigNode.Scalar(if source.Recursive then "true" else "false") ]
        @ listField "exclude" source.Excludes
        @ listField "include_globs" source.IncludeGlobs
        @ listField "exclude_globs" source.ExcludeGlobs
    )

let private ofRetention (policy: RetentionPolicy) : ConfigNode =
    ConfigNode.Mapping
        [ "keep_daily", count (RetentionCount.value policy.KeepDaily)
          "keep_weekly", count (RetentionCount.value policy.KeepWeekly)
          "keep_monthly", count (RetentionCount.value policy.KeepMonthly)
          "keep_yearly", count (RetentionCount.value policy.KeepYearly) ]

/// A limit left out is not written: the engine then uses its own default.
let private ofConcurrency (settings: Concurrency) : ConfigNode =
    let limit (key: string) (value: ConcurrencyLimit option) : (string * ConfigNode) list =
        match value with
        | Some limit -> [ (key, count (ConcurrencyLimit.value limit)) ]
        | None -> []

    ConfigNode.Mapping(limit "scan_concurrency" settings.Scan @ limit "transfer_concurrency" settings.Transfer)

let ofProfile (profile: Profile) : ConfigNode =
    ConfigNode.Mapping(
        [ "name", ConfigNode.Scalar(Profile.name profile)
          "target", ConfigNode.Scalar(AbsolutePath.value (Profile.target profile))
          "sources", ConfigNode.Sequence(Profile.sources profile |> List.map ofSource) ]
        @ (Profile.retention profile |> Option.map (fun policy -> "retention", ofRetention policy) |> Option.toList)
        @ (Profile.concurrency profile |> Option.map (fun settings -> "concurrency", ofConcurrency settings) |> Option.toList)
    )

/// The entries under `profiles`, in the order of the list.
let ofProfiles (profiles: Profile list) : ConfigNode list = profiles |> List.map ofProfile
