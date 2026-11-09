/// The text form in which loader results are recorded and compared: one line per profile, source, retention and
/// concurrency setting, or the error message. Plain functions of values.
module Vara.Engine.Tests.ProfileDescription

open System
open Vara.Domain
open Vara.Engine

/// The lines that describe one profile.
let describeProfile (profile: Profile) : string list =
    let count (value: RetentionCount) = string (RetentionCount.value value)
    let limit (value: ConcurrencyLimit option) = value |> Option.map (ConcurrencyLimit.value >> string) |> Option.defaultValue "-"
    let strings (items: string list) = String.Join("|", List.toSeq items)

    [ yield $"profile {Profile.name profile} target {AbsolutePath.value (Profile.target profile)}"

      for source in Profile.sources profile do
          yield
              $"source {AbsolutePath.value source.Path} recursive {source.Recursive} "
              + $"exclude [{strings source.Excludes}] include [{strings source.IncludeGlobs}] exclude-globs [{strings source.ExcludeGlobs}]"

      match Profile.retention profile with
      | Some policy ->
          yield
              $"retention {count policy.KeepDaily} {count policy.KeepWeekly} {count policy.KeepMonthly} {count policy.KeepYearly}"
      | None -> yield "retention none"

      match Profile.concurrency profile with
      | Some settings -> yield $"concurrency {limit settings.Scan} {limit settings.Transfer}"
      | None -> yield "concurrency none" ]

/// The placeholder that stands for the configuration file's path in a recorded message.
let configPlaceholder = "<config>"

/// A load result as recorded text: `ok` and the profile lines, or `error` and the message. The path of the file is
/// replaced by a placeholder, because it differs from one run to the next.
let resultText (configPath: string) (result: Result<string list list, string>) : string =
    match result with
    | Ok profiles -> String.Join("\n", "ok" :: List.concat profiles)
    | Error message -> "error\n" + message.Replace(configPath, configPlaceholder)

/// What the F# loader gives for a file, as recorded text.
let loadText (configPath: string) : string =
    ConfigFile.load configPath
    |> Result.map (List.map describeProfile)
    |> Result.mapError ConfigError.render
    |> resultText configPath
