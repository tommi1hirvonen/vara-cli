namespace Vara.Domain

open System

/// A source as the user has typed it. Its fields can be missing, so the screen always has something to show; the
/// field names differ from `Source`'s so that a record expression never means the wrong type.
type SourceDraft =
    { SourcePath: string option
      IsRecursive: bool
      ExcludeEntries: string list
      IncludeGlobEntries: string list
      ExcludeGlobEntries: string list }

/// The four counts as text. They stay text until the loader's invariant parser reads them, so the editor reads a
/// number exactly as the file would.
type RetentionDraft =
    { DailyText: string
      WeeklyText: string
      MonthlyText: string
      YearlyText: string }

/// A limit not set is left out of the file.
type ConcurrencyDraft =
    { ScanText: string option
      TransferText: string option }

/// A profile as the user has typed it: raw entries, in any state, from empty to valid. A field not set is `None`,
/// which is not the same as an empty entry. `Original` is the saved name the draft replaces; it never follows a rename.
type ProfileDraft =
    { Original: string option
      EnteredName: string option
      EnteredTarget: string option
      DraftSources: SourceDraft list
      RetentionEntry: RetentionDraft option
      ConcurrencyEntry: ConcurrencyDraft option }

/// One of the three lists of a source.
[<RequireQualifiedAccess>]
type StringList =
    | Excludes
    | IncludeGlobs
    | ExcludeGlobs

[<RequireQualifiedAccess>]
type RetentionKey =
    | Daily
    | Weekly
    | Monthly
    | Yearly

[<RequireQualifiedAccess>]
type ConcurrencyKey =
    | Scan
    | Transfer

module SourceDraft =
    let empty: SourceDraft =
        { SourcePath = None
          IsRecursive = true
          ExcludeEntries = []
          IncludeGlobEntries = []
          ExcludeGlobEntries = [] }

    let ofSource (source: Source) : SourceDraft =
        { SourcePath = Some(AbsolutePath.value source.Path)
          IsRecursive = source.Recursive
          ExcludeEntries = source.Excludes
          IncludeGlobEntries = source.IncludeGlobs
          ExcludeGlobEntries = source.ExcludeGlobs }

    let entries (list: StringList) (source: SourceDraft) : string list =
        match list with
        | StringList.Excludes -> source.ExcludeEntries
        | StringList.IncludeGlobs -> source.IncludeGlobEntries
        | StringList.ExcludeGlobs -> source.ExcludeGlobEntries

    let withEntries (list: StringList) (values: string list) (source: SourceDraft) : SourceDraft =
        match list with
        | StringList.Excludes -> { source with ExcludeEntries = values }
        | StringList.IncludeGlobs -> { source with IncludeGlobEntries = values }
        | StringList.ExcludeGlobs -> { source with ExcludeGlobEntries = values }

    let private listField (key: string) (values: string list) : (string * ConfigNode) list =
        match values with
        | [] -> []
        | _ -> [ (key, ConfigNode.Sequence(values |> List.map ConfigNode.Scalar)) ]

    /// The tree the loader would read for this source. A path not set is a missing `path`, which the loader rejects.
    let toNode (source: SourceDraft) : ConfigNode =
        ConfigNode.Mapping(
            (source.SourcePath |> Option.map (fun path -> "path", ConfigNode.Scalar path) |> Option.toList)
            @ [ ("recursive", ConfigNode.Scalar(if source.IsRecursive then "true" else "false")) ]
            @ listField "exclude" source.ExcludeEntries
            @ listField "include_globs" source.IncludeGlobEntries
            @ listField "exclude_globs" source.ExcludeGlobEntries
        )

module RetentionDraft =
    /// A policy that keeps nothing in any tier: what the loader reads for a count that is left out.
    let empty: RetentionDraft =
        { DailyText = "0"
          WeeklyText = "0"
          MonthlyText = "0"
          YearlyText = "0" }

    let set (key: RetentionKey) (text: string) (retention: RetentionDraft) : RetentionDraft =
        match key with
        | RetentionKey.Daily -> { retention with DailyText = text }
        | RetentionKey.Weekly -> { retention with WeeklyText = text }
        | RetentionKey.Monthly -> { retention with MonthlyText = text }
        | RetentionKey.Yearly -> { retention with YearlyText = text }

    let get (key: RetentionKey) (retention: RetentionDraft) : string =
        match key with
        | RetentionKey.Daily -> retention.DailyText
        | RetentionKey.Weekly -> retention.WeeklyText
        | RetentionKey.Monthly -> retention.MonthlyText
        | RetentionKey.Yearly -> retention.YearlyText

module ConcurrencyDraft =
    let empty: ConcurrencyDraft = { ScanText = None; TransferText = None }

    let set (key: ConcurrencyKey) (text: string option) (concurrency: ConcurrencyDraft) : ConcurrencyDraft =
        match key with
        | ConcurrencyKey.Scan -> { concurrency with ScanText = text }
        | ConcurrencyKey.Transfer -> { concurrency with TransferText = text }

    let get (key: ConcurrencyKey) (concurrency: ConcurrencyDraft) : string option =
        match key with
        | ConcurrencyKey.Scan -> concurrency.ScanText
        | ConcurrencyKey.Transfer -> concurrency.TransferText

module ProfileDraft =
    /// "Add new profile": nothing set, no sources, no retention policy, no concurrency settings.
    let empty: ProfileDraft =
        { Original = None
          EnteredName = None
          EnteredTarget = None
          DraftSources = []
          RetentionEntry = None
          ConcurrencyEntry = None }

    let private countText (count: int) : string = count.ToString Globalization.CultureInfo.InvariantCulture

    /// A draft that holds a saved profile's values.
    let ofProfile (profile: Profile) : ProfileDraft =
        { Original = Some(Profile.name profile)
          EnteredName = Some(Profile.name profile)
          EnteredTarget = Some(AbsolutePath.value (Profile.target profile))
          DraftSources = Profile.sources profile |> List.map SourceDraft.ofSource
          RetentionEntry =
            Profile.retention profile
            |> Option.map (fun policy ->
                { DailyText = countText (RetentionCount.value policy.KeepDaily)
                  WeeklyText = countText (RetentionCount.value policy.KeepWeekly)
                  MonthlyText = countText (RetentionCount.value policy.KeepMonthly)
                  YearlyText = countText (RetentionCount.value policy.KeepYearly) })
          ConcurrencyEntry =
            Profile.concurrency profile
            |> Option.map (fun settings ->
                { ScanText = settings.Scan |> Option.map (ConcurrencyLimit.value >> countText)
                  TransferText = settings.Transfer |> Option.map (ConcurrencyLimit.value >> countText) }) }

    /// The tree the loader would read for this draft: the same keys, the entries as they were typed.
    let toNode (draft: ProfileDraft) : ConfigNode =
        let scalar key (value: string option) =
            value |> Option.map (fun text -> key, ConfigNode.Scalar text) |> Option.toList

        let retention =
            draft.RetentionEntry
            |> Option.map (fun entry ->
                "retention",
                ConfigNode.Mapping
                    [ "keep_daily", ConfigNode.Scalar entry.DailyText
                      "keep_weekly", ConfigNode.Scalar entry.WeeklyText
                      "keep_monthly", ConfigNode.Scalar entry.MonthlyText
                      "keep_yearly", ConfigNode.Scalar entry.YearlyText ])
            |> Option.toList

        let concurrency =
            draft.ConcurrencyEntry
            |> Option.map (fun entry ->
                "concurrency",
                ConfigNode.Mapping(scalar "scan_concurrency" entry.ScanText @ scalar "transfer_concurrency" entry.TransferText))
            |> Option.toList

        ConfigNode.Mapping(
            scalar "name" draft.EnteredName
            @ scalar "target" draft.EnteredTarget
            @ [ ("sources", ConfigNode.Sequence(draft.DraftSources |> List.map SourceDraft.toNode)) ]
            @ retention
            @ concurrency
        )

    /// Where the saved profile that the draft replaces is in the list: found by the original name, never by the
    /// current one. A new profile, and an original that is no longer saved, replace nothing.
    let positionOf (saved: Profile list) (draft: ProfileDraft) : int option =
        draft.Original
        |> Option.bind (fun original ->
            saved |> List.tryFindIndex (fun profile -> String.Equals(Profile.name profile, original, StringComparison.OrdinalIgnoreCase)))

    /// The draft validated by the loader's own function, against the saved profiles with this draft in place of the
    /// profile it replaces (or after them, for a new one). One function answers "is it valid" for the screen, for Save
    /// and for the loader, so they cannot disagree; the result depends on the arguments and on nothing before them.
    let validate (saved: Profile list) (draft: ProfileDraft) : Result<Profile, ConfigError> =
        let nodes = ProfileNodes.ofProfiles saved

        let entries, index =
            match positionOf saved draft with
            | Some position -> nodes |> List.mapi (fun at node -> if at = position then toNode draft else node), position
            | None -> nodes @ [ toNode draft ], List.length nodes

        ProfileValidation.validateProfiles entries |> Result.map (List.item index)

    /// The saved profiles with the validated profile in place of the one the draft replaces, or added at the end.
    let place (saved: Profile list) (draft: ProfileDraft) (profile: Profile) : Profile list =
        match positionOf saved draft with
        | Some position -> saved |> List.mapi (fun at existing -> if at = position then profile else existing)
        | None -> saved @ [ profile ]

    /// Applies the change to the source at the index; a draft with no source there is returned as it is.
    let updateSource (index: int) (change: SourceDraft -> SourceDraft) (draft: ProfileDraft) : ProfileDraft =
        { draft with DraftSources = draft.DraftSources |> List.mapi (fun at source -> if at = index then change source else source) }

    let removeSource (index: int) (draft: ProfileDraft) : ProfileDraft =
        { draft with DraftSources = draft.DraftSources |> List.indexed |> List.filter (fun (at, _) -> at <> index) |> List.map snd }

    /// What the editor says about a validation error. The loader's duplicate-name message speaks of the file; the draft
    /// is not in it yet, so the editor says it in terms of the profile being edited.
    let describe (error: ConfigError) : string =
        match error with
        | ConfigError.DuplicateName name ->
            $"A profile named '{name}' already exists. Choose a different name to save this profile."
        | ConfigError.FileNotFound _
        | ConfigError.Malformed _
        | ConfigError.InvalidProfile _
        | ConfigError.UnknownProfile _
        | ConfigError.ProfileNameRequired -> ConfigError.render error
