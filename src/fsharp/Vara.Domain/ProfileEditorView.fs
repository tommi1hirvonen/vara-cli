namespace Vara.Domain

/// What the loop asks the user on a screen. A choice either sends an input or leads to a further question, so that
/// "Edit name" asks for the text it needs; the loop turns the user's answers into one `Input`.
[<RequireQualifiedAccess>]
type Question =
    /// `Choose title choices`: the first choice is highlighted.
    | Choose of title: string * choices: (Span list * Answer) list
    /// A line of text; a blank entry is `None`.
    | Text of prompt: string * answer: (string option -> Input)
    /// A yes or no question, whose default is no.
    | Confirm of text: string * yes: Input * no: Input

and [<RequireQualifiedAccess>] Answer =
    | Send of Input
    | Ask of Question

/// The screens of the profile editor as plain data, and the question each asks. Every screen is a function of the state
/// alone, so a screen that is shown again after a clear is complete (profile-management: "Profile fields are editable in
/// any order"). The texts the spec does not fix are the C# build's: `ProfilesCommand` and `ProfileDraftPresenter`.
module ProfileEditorView =
    // ---- Texts (the C# build's) ----

    /// How a field with no value is shown (`ProfileDraftPresenter.FormatOptional`).
    let private notSet = "(not set)"
    /// How an empty list is shown (`ProfileDraftPresenter.FormatList`).
    let private noEntries = "(none)"
    let private mainTitle = "Vara profiles - select a profile to edit, or an action:"
    let private addProfile = "Add new profile"
    let private deleteProfile = "Delete a profile"
    let private quit = "Quit"
    let private deleteTitle = "Select a profile to delete:"
    let private cancel = "Cancel"
    let private fieldsTitle = "Choose a field to edit, or Save/Discard:"
    let private save = "Save"
    let private discard = "Discard"
    let private sourcesTitle = "Sources:"
    let private addSource = "Add source"
    let private back = "Back"
    let private sourceTitle = "Edit source:"
    let private removeSource = "Remove this source"
    let private done_ = "Done"
    let private addEntry = "Add entry"
    /// Said under the draft when it has no validation error (`ProfileDraftPresenter.Render`).
    let private valid = "Draft is valid."
    let private validationError = "Validation error: "
    let private cannotSave = "Cannot save - validation error: "
    let private errorPrefix = "Error: "
    let private blankClears = "leave blank to clear"
    let private blankDefault = "leave blank for the pipeline's own default"

    let private span (tone: Tone) (text: string) : Span = { Tone = tone; Text = text }

    let private optional (value: string option) : string = defaultArg value notSet

    /// A row of the form "Label: value", the value in the detail tone when it is not set.
    let private field (label: string) (value: string option) : Line =
        Line.Spans [ span Tone.Plain (label + ": "); span (if value.IsSome then Tone.Plain else Tone.Detail) (optional value) ]

    let private noticeLine (notice: Notice) : Line =
        match notice with
        | Notice.Saved name -> Line.Text(Tone.Success, $"Saved profile '{name}'.")
        | Notice.Deleted name -> Line.Text(Tone.Success, $"Deleted profile '{name}'.")
        | Notice.CannotSave error -> Line.Text(Tone.Error, cannotSave + ProfileDraft.describe error)
        | Notice.WriteFailed error -> Line.Text(Tone.Error, errorPrefix + ConfigWriteError.render error)

    let private noticeLines (notice: Notice option) : Line list =
        match notice with
        | Some notice -> [ noticeLine notice; Line.Text(Tone.Plain, "") ]
        | None -> []

    let private validity (session: Session) : Line =
        match ProfileDraft.validate session.Saved session.Draft with
        | Ok _ -> Line.Text(Tone.Success, valid)
        | Error error -> Line.Text(Tone.Error, validationError + ProfileDraft.describe error)

    let private listText (values: string list) : string =
        match values with
        | [] -> noEntries
        | _ -> String.concat ", " values

    let private sourceSummary (source: SourceDraft) : string =
        $"{optional source.SourcePath} (recursive: {source.IsRecursive}, exclude: {List.length source.ExcludeEntries}, "
        + $"include_globs: {List.length source.IncludeGlobEntries}, exclude_globs: {List.length source.ExcludeGlobEntries})"

    let private retentionText (retention: RetentionDraft option) : string =
        match retention with
        | Some entry ->
            $"keep_daily={entry.DailyText}, keep_weekly={entry.WeeklyText}, keep_monthly={entry.MonthlyText}, keep_yearly={entry.YearlyText}"
        | None -> "not set"

    let private concurrencyText (concurrency: ConcurrencyDraft option) : string =
        match concurrency with
        | Some entry ->
            $"scan_concurrency={optional entry.ScanText}, transfer_concurrency={optional entry.TransferText}"
        | None -> "not set"

    let private sourcesTable (sources: SourceDraft list) : Line list =
        match sources with
        | [] -> []
        | _ ->
            let column header : Column = { Header = header; Align = Align.Left }

            [ Line.Table
                  { Columns = [ column "#"; column "Path"; column "Recursive"; column "Exclude"; column "Include globs"; column "Exclude globs" ]
                    Rows =
                      sources
                      |> List.mapi (fun index source ->
                          [ span Tone.Plain (string (index + 1))
                            span (if source.SourcePath.IsSome then Tone.Plain else Tone.Detail) (optional source.SourcePath)
                            span Tone.Plain (string source.IsRecursive)
                            span Tone.Plain (string (List.length source.ExcludeEntries))
                            span Tone.Plain (string (List.length source.IncludeGlobEntries))
                            span Tone.Plain (string (List.length source.ExcludeGlobEntries)) ]) } ]

    let private draftBox (session: Session) : Line =
        let draft = session.Draft

        Line.Box(
            optional draft.EnteredName,
            [ field "Name" draft.EnteredName
              field "Target root" draft.EnteredTarget
              Line.Text(Tone.Plain, $"Sources: {List.length draft.DraftSources}") ]
            @ sourcesTable draft.DraftSources
            @ [ Line.Text(Tone.Plain, "Retention: " + retentionText draft.RetentionEntry)
                Line.Text(Tone.Plain, "Concurrency: " + concurrencyText draft.ConcurrencyEntry)
                validity session ]
        )

    let private profilesTable (profiles: Profile list) : Line list =
        match profiles with
        | [] -> [ Line.Text(Tone.Detail, "No profiles are configured yet.") ]
        | _ ->
            let column header : Column = { Header = header; Align = Align.Left }

            [ Line.Table
                  { Columns = [ column "Name"; column "Target root"; { Header = "Sources"; Align = Align.Right } ]
                    Rows =
                      profiles
                      |> List.map (fun profile ->
                          [ span Tone.Plain (Profile.name profile)
                            span Tone.Plain (AbsolutePath.value (Profile.target profile))
                            span Tone.Plain (string (List.length (Profile.sources profile))) ]) } ]

    let private sourceBox (session: Session) (source: SourceDraft) : Line list =
        [ field "Path" source.SourcePath
          Line.Text(Tone.Plain, $"Recursive: {source.IsRecursive}")
          Line.Text(Tone.Plain, "Exclude: " + listText source.ExcludeEntries)
          Line.Text(Tone.Plain, "Include globs: " + listText source.IncludeGlobEntries)
          Line.Text(Tone.Plain, "Exclude globs: " + listText source.ExcludeGlobEntries) ]
        @ (match ProfileDraft.validate session.Saved session.Draft with
           | Ok _ -> []
           | Error error -> [ Line.Text(Tone.Error, validationError + ProfileDraft.describe error) ])

    /// The screen for a state. The main menu lists every saved profile by name, target root and source count.
    let view (state: EditorState) : Display =
        match state with
        | EditorState.MainMenu(profiles, notice) -> noticeLines notice @ profilesTable profiles
        | EditorState.PickingDelete _
        | EditorState.ConfirmingDelete _
        | EditorState.Persisting _
        | EditorState.Exited -> []
        | EditorState.Editing(session, page) ->
            noticeLines session.Notice
            @ (match page with
               | Page.Fields
               | Page.Sources
               | Page.Retention
               | Page.Concurrency -> [ draftBox session ]
               | Page.Source index ->
                   List.tryItem index session.Draft.DraftSources
                   |> Option.map (sourceBox session)
                   |> Option.defaultValue []
               | Page.Entries(index, list) ->
                   [ Line.Text(Tone.Plain, "Current entries: " + listText (List.tryItem index session.Draft.DraftSources |> Option.map (SourceDraft.entries list) |> Option.defaultValue [])) ])

    // ---- Questions ----

    let private plain (text: string) : Span list = [ span Tone.Plain text ]

    let private send (input: Input) : Answer = Answer.Send input

    let private mainMenu (profiles: Profile list) : Question =
        let profileChoices =
            profiles
            |> List.mapi (fun index profile ->
                plain $"{Profile.name profile} - {AbsolutePath.value (Profile.target profile)} ({List.length (Profile.sources profile)} source(s))",
                send (Input.Menu(MenuInput.Open index)))

        let deleteChoice =
            if List.isEmpty profiles then [] else [ plain deleteProfile, send (Input.Menu MenuInput.StartDelete) ]

        Question.Choose(
            mainTitle,
            profileChoices
            @ [ plain addProfile, send (Input.Menu MenuInput.Add) ]
            @ deleteChoice
            @ [ plain quit, send (Input.Menu MenuInput.Quit) ]
        )

    let private deletePicker (profiles: Profile list) : Question =
        Question.Choose(
            deleteTitle,
            (profiles
             |> List.mapi (fun index profile ->
                 plain $"{Profile.name profile} - {AbsolutePath.value (Profile.target profile)}", send (Input.Picker(PickerInput.Pick index))))
            @ [ plain cancel, send (Input.Picker PickerInput.Cancel) ]
        )

    /// The question that names the profile and its target root, and says that backup data is not touched.
    let private confirmDelete (profile: Profile) : Question =
        Question.Confirm(
            $"Delete profile '{Profile.name profile}' (target '{AbsolutePath.value (Profile.target profile)}')? "
            + "This will not touch any backup data already written under that target.",
            Input.Confirm ConfirmInput.Delete,
            Input.Confirm ConfirmInput.Keep
        )

    let private textPrompt (label: string) (current: string) (hint: string) (answer: string option -> Input) : Answer =
        Answer.Ask(Question.Text($"{label} (currently: {current}; {hint}):", answer))

    let private fieldsQuestion (session: Session) : Question =
        let draft = session.Draft
        let input (fieldsInput: FieldsInput) : Input = Input.Fields fieldsInput
        let retention = if draft.RetentionEntry.IsSome then "configured" else "not set"
        let concurrency = if draft.ConcurrencyEntry.IsSome then "configured" else "not set"

        Question.Choose(
            fieldsTitle,
            [ plain $"Edit name (currently: {optional draft.EnteredName})",
              textPrompt "Name" (optional draft.EnteredName) blankClears (fun text -> input (FieldsInput.SetName text))
              plain $"Edit target root (currently: {optional draft.EnteredTarget})",
              textPrompt "Target root" (optional draft.EnteredTarget) blankClears (fun text -> input (FieldsInput.SetTarget text))
              plain $"Manage sources ({List.length draft.DraftSources} configured)", send (input FieldsInput.ManageSources)
              plain $"Retention ({retention})", send (input FieldsInput.EditRetention)
              plain $"Concurrency ({concurrency})", send (input FieldsInput.EditConcurrency)
              plain save, send (input FieldsInput.Save)
              plain discard, send (input FieldsInput.Discard) ]
        )

    let private sourcesQuestion (session: Session) : Question =
        let existing =
            session.Draft.DraftSources
            |> List.mapi (fun index source ->
                plain $"{index + 1}. {sourceSummary source}", send (Input.Sources(SourcesInput.Open index)))

        Question.Choose(
            sourcesTitle,
            existing
            @ [ plain addSource, send (Input.Sources SourcesInput.Add)
                plain back, send (Input.Sources SourcesInput.Back) ]
        )

    let private sourceQuestion (source: SourceDraft) : Question =
        let input (sourceInput: SourceInput) : Input = Input.Source sourceInput
        let count (values: string list) = List.length values

        Question.Choose(
            sourceTitle,
            [ plain $"Edit path (currently: {optional source.SourcePath})",
              textPrompt "Path" (optional source.SourcePath) blankClears (fun text -> input (SourceInput.SetPath text))
              plain $"Toggle recursive (currently: {source.IsRecursive})", send (input (SourceInput.SetRecursive(not source.IsRecursive)))
              plain $"Edit exclude list ({count source.ExcludeEntries} entries)", send (input (SourceInput.OpenList StringList.Excludes))
              plain $"Edit include-glob list ({count source.IncludeGlobEntries} entries)",
              send (input (SourceInput.OpenList StringList.IncludeGlobs))
              plain $"Edit exclude-glob list ({count source.ExcludeGlobEntries} entries)",
              send (input (SourceInput.OpenList StringList.ExcludeGlobs))
              plain removeSource, send (input SourceInput.Remove)
              plain back, send (input SourceInput.Back) ]
        )

    /// What an entry of each list is called in the prompts (the C# build's wording).
    let private itemLabel (list: StringList) : string =
        match list with
        | StringList.Excludes -> "exclude entry"
        | StringList.IncludeGlobs -> "include-glob pattern"
        | StringList.ExcludeGlobs -> "exclude-glob pattern"

    let private entriesQuestion (list: StringList) (values: string list) : Question =
        let input (entriesInput: EntriesInput) : Input = Input.Entries entriesInput
        let label = itemLabel list

        Question.Choose(
            $"Manage {label} list:",
            [ plain addEntry, Answer.Ask(Question.Text($"New {label}:", fun text -> input (EntriesInput.Add(defaultArg text "")))) ]
            @ (values |> List.mapi (fun index value -> plain $"Remove {index + 1}. {value}", send (input (EntriesInput.Remove index))))
            @ [ plain done_, send (input EntriesInput.Done) ]
        )

    let private retentionQuestion (retention: RetentionDraft option) : Question =
        let input (retentionInput: RetentionInput) : Input = Input.Retention retentionInput
        let current = defaultArg retention RetentionDraft.empty

        let keep (key: RetentionKey) (name: string) =
            plain $"Set {name} (currently: {RetentionDraft.get key current})",
            textPrompt name (RetentionDraft.get key current) "leave blank for 0" (fun text -> input (RetentionInput.Set(key, text)))

        Question.Choose(
            "Retention policy:",
            [ keep RetentionKey.Daily "keep_daily"
              keep RetentionKey.Weekly "keep_weekly"
              keep RetentionKey.Monthly "keep_monthly"
              keep RetentionKey.Yearly "keep_yearly"
              plain "Clear the retention policy", send (input RetentionInput.Clear)
              plain back, send (input RetentionInput.Back) ]
        )

    let private concurrencyQuestion (concurrency: ConcurrencyDraft option) : Question =
        let input (concurrencyInput: ConcurrencyInput) : Input = Input.Concurrency concurrencyInput
        let current = defaultArg concurrency ConcurrencyDraft.empty

        let limit (key: ConcurrencyKey) (name: string) =
            plain $"Set {name} (currently: {optional (ConcurrencyDraft.get key current)})",
            textPrompt name (optional (ConcurrencyDraft.get key current)) blankDefault (fun text -> input (ConcurrencyInput.Set(key, text)))

        Question.Choose(
            "Concurrency settings:",
            [ limit ConcurrencyKey.Scan "scan_concurrency"
              limit ConcurrencyKey.Transfer "transfer_concurrency"
              plain "Clear the concurrency settings", send (input ConcurrencyInput.Clear)
              plain back, send (input ConcurrencyInput.Back) ]
        )

    /// What to ask on a screen; nothing while a write is out or once the editor has exited.
    let ask (state: EditorState) : Question option =
        match state with
        | EditorState.MainMenu(profiles, _) -> Some(mainMenu profiles)
        | EditorState.PickingDelete profiles -> Some(deletePicker profiles)
        | EditorState.ConfirmingDelete(_, profile) -> Some(confirmDelete profile)
        | EditorState.Persisting _
        | EditorState.Exited -> None
        | EditorState.Editing(session, page) ->
            match page with
            | Page.Fields -> Some(fieldsQuestion session)
            | Page.Sources -> Some(sourcesQuestion session)
            | Page.Source index -> List.tryItem index session.Draft.DraftSources |> Option.map sourceQuestion
            | Page.Entries(index, list) ->
                List.tryItem index session.Draft.DraftSources
                |> Option.map (fun source -> entriesQuestion list (SourceDraft.entries list source))
            | Page.Retention -> Some(retentionQuestion session.Draft.RetentionEntry)
            | Page.Concurrency -> Some(concurrencyQuestion session.Draft.ConcurrencyEntry)
