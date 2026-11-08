/// Loads one YAML corpus with the C# YamlProfileConfigLoader and with the F# loader. Each file must give
/// equal profiles or the same error message.
module Vara.Engine.Tests.ParityTests

open System
open System.IO
open Xunit
open Vara.Domain
open Vara.Engine

/// A valid profile block followed by extra lines, for corpus entries that vary one detail.
let private withExtra (extra: string) : string =
    "profiles:
  - name: p
    target: C:\Backups\p
    sources:
      - path: C:\Data\p
" + extra

let private corpus: (string * string) list =
    [ "documented sample",
      """profiles:
  - name: Documents
    target: C:/Backups/Docs/
    sources:
      - path: C:/Users/me/Documents
        recursive: false
        exclude: [C:\Users\me\Documents\tmp]
        include_globs: ["*.docx", "*.xlsx"]
        exclude_globs: ["~$*"]
      - path: C:\Users\me\Pictures\
    retention:
      keep_daily: 7
      keep_weekly: 4
      keep_monthly: 12
      keep_yearly: 3
    concurrency:
      scan_concurrency: 4
      transfer_concurrency: 8
  - name: Second
    target: D:\Mirror
    sources:
      - path: E:\Data\.\x\..\y
"""
      "key casing",
      "Profiles:\n  - NAME: p\n    Target: C:\\t\n    SOURCES:\n      - PATH: C:\\s\n        Recursive: FALSE\n        EXCLUDE: [a]\n        Include_Globs: [b]\n    Retention: {KEEP_DAILY: 3}\n    CONCURRENCY: {Scan_Concurrency: 2}\n"
      "recursive True", withExtra "        recursive: True\n"
      "recursive no", withExtra "        recursive: 'no'\n"
      "recursive 1", withExtra "        recursive: 1\n"
      "recursive mapping", withExtra "        recursive: {a: b}\n"
      "empty file", ""
      "only comments", "# nothing here\n# at all\n"
      "no profiles key", "other: 1\n"
      "profiles null", "profiles:\n"
      "profiles empty list", "profiles: []\n"
      "profiles mapping", "profiles:\n  name: x\n"
      "profiles scalar", "profiles: text\n"
      "root sequence", "- a\n- b\n"
      "root scalar", "just text\n"
      "yaml syntax error", "profiles:\n  - name: \"unclosed\n"
      "yaml bad indentation", "profiles:\n  - name: p\n   target: x\n"
      "entry is a scalar", "profiles:\n  - text\n"
      "entry is a sequence", "profiles:\n  - [a, b]\n"
      "missing name", "profiles:\n  - target: C:\\t\n    sources:\n      - path: C:\\s\n"
      "blank name", "profiles:\n  - name: '  '\n    target: C:\\t\n    sources:\n      - path: C:\\s\n"
      "name is a mapping", "profiles:\n  - name: {a: b}\n    target: C:\\t\n    sources:\n      - path: C:\\s\n"
      "missing target", "profiles:\n  - name: p\n    sources:\n      - path: C:\\s\n"
      "null target", "profiles:\n  - name: p\n    target:\n    sources:\n      - path: C:\\s\n"
      "sources missing", "profiles:\n  - name: p\n    target: C:\\t\n"
      "sources empty", "profiles:\n  - name: p\n    target: C:\\t\n    sources: []\n"
      "sources scalar", "profiles:\n  - name: p\n    target: C:\\t\n    sources: C:\\s\n"
      "source entry scalar", "profiles:\n  - name: p\n    target: C:\\t\n    sources:\n      - C:\\s\n"
      "source without path", "profiles:\n  - name: p\n    target: C:\\t\n    sources:\n      - recursive: true\n"
      "relative target", "profiles:\n  - name: p\n    target: backup\\\n    sources:\n      - path: C:\\s\n"
      "relative dotted target", "profiles:\n  - name: p\n    target: ..\\backup\n    sources:\n      - path: C:\\s\n"
      "drive relative target", "profiles:\n  - name: p\n    target: C:backup\n    sources:\n      - path: C:\\s\n"
      "relative source", "profiles:\n  - name: p\n    target: C:\\t\n    sources:\n      - path: Documents\n"
      "relative dotted source", "profiles:\n  - name: p\n    target: C:\\t\n    sources:\n      - path: .\\src\n"
      "exclude scalar", withExtra "        exclude: '*.tmp'\n"
      "include_globs scalar", withExtra "        include_globs: '*.cs'\n"
      "exclude_globs mapping", withExtra "        exclude_globs: {a: b}\n"
      "exclude entry mapping", withExtra "        exclude:\n          - {a: b}\n"
      "exclude entry sequence", withExtra "        exclude_globs:\n          - [a, b]\n"
      "exclude null entry", withExtra "        exclude: [~]\n"
      "exclude empty list", withExtra "        exclude: []\n"
      "anchor and alias", withExtra "        exclude: &x [a, b]\n        exclude_globs: *x\n"
      "target equals source", "profiles:\n  - name: p\n    target: C:\\Data\n    sources:\n      - path: C:\\Data\n"
      "target inside source", "profiles:\n  - name: p\n    target: C:\\Users\\me\\backup\n    sources:\n      - path: C:\\Users\\me\\\n"
      "source inside target", "profiles:\n  - name: p\n    target: C:\\Users\\me\\backup\n    sources:\n      - path: C:\\Users\\me\\backup\\Documents\n"
      "overlap differs by case and separator", "profiles:\n  - name: p\n    target: c:\\backup\\\n    sources:\n      - path: C:\\Backup\n"
      "overlap of the drive root", "profiles:\n  - name: p\n    target: C:\\backup\n    sources:\n      - path: C:\\\n"
      "overlap with another profile's source", "profiles:\n  - name: a\n    target: C:\\Data\\t\n    sources:\n      - path: C:\\s\n  - name: b\n    target: C:\\u\n    sources:\n      - path: C:\\Data\n"
      "identical sources", "profiles:\n  - name: p\n    target: C:\\t\n    sources:\n      - path: C:\\Data\n      - path: C:\\Data\n"
      "nested sources", "profiles:\n  - name: p\n    target: C:\\t\n    sources:\n      - path: C:\\Users\\me\n      - path: C:\\Users\\me\\Documents\n"
      "nested sources reversed", "profiles:\n  - name: p\n    target: C:\\t\n    sources:\n      - path: C:\\Users\\me\\Documents\n      - path: C:\\Users\\me\n"
      "sources differ by case and separator", "profiles:\n  - name: p\n    target: C:\\t\n    sources:\n      - path: C:\\Data\\\n      - path: c:\\Data\n"
      "similar sources do not overlap", "profiles:\n  - name: p\n    target: C:\\t\n    sources:\n      - path: C:\\Data\n      - path: C:\\Database\n"
      "duplicate names", "profiles:\n  - name: Work\n    target: C:\\t1\n    sources:\n      - path: C:\\a\n  - name: work\n    target: C:\\t2\n    sources:\n      - path: C:\\b\n"
      "duplicate names after an invalid-looking later entry", "profiles:\n  - name: a\n    target: C:\\t1\n    sources:\n      - path: C:\\a\n  - name: a\n    target: relative\n    sources:\n      - path: C:\\b\n"
      "retention not an integer", withExtra "    retention:\n      keep_weekly: abc\n"
      "retention negative", withExtra "    retention:\n      keep_yearly: -1\n"
      "retention zero", withExtra "    retention:\n      keep_daily: 0\n"
      "retention empty mapping", withExtra "    retention: {}\n"
      "retention scalar", withExtra "    retention: 5\n"
      "retention value padded", withExtra "    retention:\n      keep_daily: ' 5 '\n"
      "retention value signed", withExtra "    retention:\n      keep_daily: +5\n"
      "retention value nested", withExtra "    retention:\n      keep_daily: [1]\n"
      "retention value too large", withExtra "    retention:\n      keep_daily: 99999999999\n"
      "concurrency not an integer", withExtra "    concurrency:\n      scan_concurrency: abc\n"
      "concurrency zero", withExtra "    concurrency:\n      transfer_concurrency: 0\n"
      "concurrency negative", withExtra "    concurrency:\n      scan_concurrency: -3\n"
      "concurrency scan only", withExtra "    concurrency:\n      scan_concurrency: 4\n"
      "concurrency transfer only", withExtra "    concurrency:\n      transfer_concurrency: 8\n"
      "concurrency empty mapping", withExtra "    concurrency: {}\n"
      "concurrency scalar", withExtra "    concurrency: 5\n"
      "order: concurrency error before relative target", "profiles:\n  - name: p\n    target: relative\n    concurrency:\n      scan_concurrency: abc\n    sources:\n      - path: C:\\s\n"
      "order: source error before missing target path", "profiles:\n  - name: p\n    target: relative\n    sources:\n      - path: relative\n" ]

let private describeProfile (profile: Profile) : string list =
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

let private describeCSharpProfile (profile: Vara.Core.Configuration.Profile) : string list =
    let optional (value: Nullable<int>) = if value.HasValue then string value.Value else "-"
    let strings (items: Collections.Generic.IReadOnlyList<string>) = String.Join("|", items)

    [ yield $"profile {profile.Name} target {profile.TargetRoot}"

      for source in profile.Sources do
          yield
              $"source {source.Path} recursive {source.Recursive} "
              + $"exclude [{strings source.Excludes}] include [{strings source.IncludeGlobs}] exclude-globs [{strings source.ExcludeGlobs}]"

      match Option.ofObj profile.Retention with
      | Some policy -> yield $"retention {policy.KeepDaily} {policy.KeepWeekly} {policy.KeepMonthly} {policy.KeepYearly}"
      | None -> yield "retention none"

      match Option.ofObj profile.Concurrency with
      | Some settings -> yield $"concurrency {optional settings.ScanConcurrency} {optional settings.TransferConcurrency}"
      | None -> yield "concurrency none" ]

/// ArgumentException appends " (Parameter 'name')" to its message, so the C# overlap and path messages end with
/// the name of a constructor parameter. The F# messages leave that suffix out; it is the one deliberate difference.
let private withoutParameterSuffix (message: string) : string =
    Text.RegularExpressions.Regex.Replace(message, @" \(Parameter '[^']*'\)$", "")

let private loadWithCSharp (configPath: string) : Result<string list list, string> =
    try
        Vara.Infrastructure.Configuration.YamlProfileConfigLoader().LoadProfiles configPath
        |> Seq.map describeCSharpProfile
        |> List.ofSeq
        |> Ok
    with
    | :? Vara.Core.Configuration.ProfileConfigException as error -> Error(withoutParameterSuffix error.Message)
    | error -> Error $"unexpected {error.GetType().Name}: {error.Message}"

let private loadWithFSharp (configPath: string) : Result<string list list, string> =
    ConfigFile.load configPath
    |> Result.map (List.map describeProfile)
    |> Result.mapError ConfigError.render

let private show (outcome: Result<string list list, string>) : string =
    match outcome with
    | Ok profiles -> "ok " + String.Join(" / ", profiles |> List.map (fun lines -> String.Join("; ", List.toSeq lines)))
    | Error message -> "error " + message

[<Fact>]
let ``the F# loader gives the same profiles or the same error message as the C# loader`` () =
    ConfigurationTests.withScratch (fun directory ->
        let mismatches =
            corpus
            |> List.mapi (fun index (name, text) ->
                let configPath = Path.Combine(directory, $"{index}.yml")
                File.WriteAllText(configPath, text)
                let expected, actual = loadWithCSharp configPath, loadWithFSharp configPath

                if expected = actual then
                    None
                else
                    Some $"{name}:\n  C#: {show expected}\n  F#: {show actual}")
            |> List.choose id

        Assert.True(mismatches.IsEmpty, String.Join("\n", mismatches)))

[<Fact>]
let ``the corpus exercises both successes and errors`` () =
    ConfigurationTests.withScratch (fun directory ->
        let outcomes =
            corpus
            |> List.mapi (fun index (_, text) ->
                let configPath = Path.Combine(directory, $"{index}.yml")
                File.WriteAllText(configPath, text)
                loadWithCSharp configPath)

        Assert.True(outcomes |> List.exists Result.isOk)
        Assert.True(outcomes |> List.exists Result.isError)
        Assert.True(corpus.Length >= 60))

[<Fact>]
let ``the C# loader reports a missing file with the same message`` () =
    let missing = Path.Combine(Path.GetTempPath(), "vara-tests-missing", "profiles.yml")
    Assert.Equal(loadWithCSharp missing, loadWithFSharp missing)
