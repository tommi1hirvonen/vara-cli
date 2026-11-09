namespace Vara.Domain

/// The state machine of `vara profiles`: `update : EditorState -> Input -> EditorState * Effect list`, pure. The loop
/// in the command reads an input, calls it, performs the effects and shows the next screen (design D1).
module ProfileEditor =
    /// The editor as it opens: the main menu over the saved profiles.
    let start (profiles: Profile list) : EditorState = EditorState.MainMenu(profiles, None)

    let private unchanged (state: EditorState) : EditorState * Effect list = state, []

    let private sameName (left: string) (right: string) : bool =
        System.String.Equals(left, right, System.StringComparison.OrdinalIgnoreCase)

    /// Puts a notice on the screen the editor returns to.
    let private attach (notice: Notice) (state: EditorState) : EditorState =
        match state with
        | EditorState.MainMenu(profiles, _) -> EditorState.MainMenu(profiles, Some notice)
        | EditorState.Editing(session, page) -> EditorState.Editing({ session with Notice = Some notice }, page)
        | EditorState.PickingDelete _
        | EditorState.ConfirmingDelete _
        | EditorState.Persisting _
        | EditorState.Exited -> state

    let private editing (session: Session) (page: Page) : EditorState * Effect list =
        EditorState.Editing({ session with Notice = None }, page), []

    /// An edit of the draft. The screen's validity is not stored: it is computed from the draft whenever it is shown.
    let private edited (session: Session) (page: Page) (change: ProfileDraft -> ProfileDraft) : EditorState * Effect list =
        EditorState.Editing({ session with Draft = change session.Draft; Notice = None }, page), []

    let private menu (profiles: Profile list) (input: MenuInput) : EditorState * Effect list =
        match input with
        | MenuInput.Open index ->
            match List.tryItem index profiles with
            | Some profile ->
                editing { Saved = profiles; Draft = ProfileDraft.ofProfile profile; Notice = None } Page.Fields
            | None -> unchanged (EditorState.MainMenu(profiles, None))
        | MenuInput.Add -> editing { Saved = profiles; Draft = ProfileDraft.empty; Notice = None } Page.Fields
        | MenuInput.StartDelete ->
            if List.isEmpty profiles then
                unchanged (EditorState.MainMenu(profiles, None))
            else
                EditorState.PickingDelete profiles, []
        | MenuInput.Quit -> EditorState.Exited, []

    let private picker (profiles: Profile list) (input: PickerInput) : EditorState * Effect list =
        match input with
        | PickerInput.Pick index ->
            match List.tryItem index profiles with
            | Some profile -> EditorState.ConfirmingDelete(profiles, profile), []
            | None -> unchanged (EditorState.PickingDelete profiles)
        | PickerInput.Cancel -> EditorState.MainMenu(profiles, None), []

    /// Deleting removes the profile from the list that is written; the list in the session changes when the write
    /// has succeeded.
    let private confirm (profiles: Profile list) (profile: Profile) (input: ConfirmInput) : EditorState * Effect list =
        match input with
        | ConfirmInput.Delete ->
            let candidate = profiles |> List.filter (fun other -> not (sameName (Profile.name other) (Profile.name profile)))

            EditorState.Persisting
                { Candidate = candidate
                  Done = Notice.Deleted(Profile.name profile)
                  Back = EditorState.MainMenu(profiles, None) },
            [ Effect.Persist candidate ]
        | ConfirmInput.Keep -> EditorState.MainMenu(profiles, None), []

    /// Save validates the draft as it is now, and persists the profile that validation produced: never an earlier one.
    let private save (session: Session) : EditorState * Effect list =
        match ProfileDraft.validate session.Saved session.Draft with
        | Error error -> EditorState.Editing({ session with Notice = Some(Notice.CannotSave error) }, Page.Fields), []
        | Ok profile ->
            let candidate = ProfileDraft.place session.Saved session.Draft profile

            EditorState.Persisting
                { Candidate = candidate
                  Done = Notice.Saved(Profile.name profile)
                  Back = EditorState.Editing({ session with Notice = None }, Page.Fields) },
            [ Effect.Persist candidate ]

    let private fields (session: Session) (input: FieldsInput) : EditorState * Effect list =
        match input with
        | FieldsInput.SetName name -> edited session Page.Fields (fun draft -> { draft with EnteredName = name })
        | FieldsInput.SetTarget target -> edited session Page.Fields (fun draft -> { draft with EnteredTarget = target })
        | FieldsInput.ManageSources -> editing session Page.Sources
        | FieldsInput.EditRetention -> editing session Page.Retention
        | FieldsInput.EditConcurrency -> editing session Page.Concurrency
        | FieldsInput.Save -> save session
        | FieldsInput.Discard -> EditorState.MainMenu(session.Saved, None), []

    let private sources (session: Session) (input: SourcesInput) : EditorState * Effect list =
        match input with
        | SourcesInput.Add ->
            // The source is part of the draft from this moment, with no fields filled in.
            let draft = { session.Draft with DraftSources = session.Draft.DraftSources @ [ SourceDraft.empty ] }

            EditorState.Editing({ session with Draft = draft; Notice = None }, Page.Source(List.length draft.DraftSources - 1)),
            []
        | SourcesInput.Open index ->
            if index >= 0 && index < List.length session.Draft.DraftSources then
                editing session (Page.Source index)
            else
                unchanged (EditorState.Editing(session, Page.Sources))
        | SourcesInput.Back -> editing session Page.Fields

    let private source (session: Session) (index: int) (input: SourceInput) : EditorState * Effect list =
        let page = Page.Source index

        let change (update: SourceDraft -> SourceDraft) =
            edited session page (ProfileDraft.updateSource index update)

        match input with
        | SourceInput.SetPath path -> change (fun source -> { source with SourcePath = path })
        | SourceInput.SetRecursive recursive -> change (fun source -> { source with IsRecursive = recursive })
        | SourceInput.OpenList list -> editing session (Page.Entries(index, list))
        | SourceInput.Remove -> edited session Page.Sources (ProfileDraft.removeSource index)
        | SourceInput.Back -> editing session Page.Sources

    let private entries (session: Session) (index: int) (list: StringList) (input: EntriesInput) : EditorState * Effect list =
        let page = Page.Entries(index, list)

        let change (update: string list -> string list) =
            edited
                session
                page
                (ProfileDraft.updateSource index (fun source ->
                    SourceDraft.withEntries list (update (SourceDraft.entries list source)) source))

        match input with
        | EntriesInput.Add text ->
            let entry = text.Trim()
            // A blank entry adds nothing.
            if entry.Length = 0 then
                unchanged (EditorState.Editing(session, page))
            else
                change (fun values -> values @ [ entry ])
        | EntriesInput.Remove position ->
            change (fun values -> values |> List.indexed |> List.filter (fun (at, _) -> at <> position) |> List.map snd)
        | EntriesInput.Done -> editing session (Page.Source index)

    let private retention (session: Session) (input: RetentionInput) : EditorState * Effect list =
        match input with
        | RetentionInput.Set(key, text) ->
            // A blank count is what the loader reads for a count that is left out: zero.
            let value = defaultArg text "0"

            edited session Page.Retention (fun draft ->
                { draft with
                    RetentionEntry = Some(RetentionDraft.set key value (defaultArg draft.RetentionEntry RetentionDraft.empty)) })
        | RetentionInput.Clear -> edited session Page.Retention (fun draft -> { draft with RetentionEntry = None })
        | RetentionInput.Back -> editing session Page.Fields

    let private concurrency (session: Session) (input: ConcurrencyInput) : EditorState * Effect list =
        match input with
        | ConcurrencyInput.Set(key, text) ->
            edited session Page.Concurrency (fun draft ->
                { draft with
                    ConcurrencyEntry =
                        Some(ConcurrencyDraft.set key text (defaultArg draft.ConcurrencyEntry ConcurrencyDraft.empty)) })
        | ConcurrencyInput.Clear -> edited session Page.Concurrency (fun draft -> { draft with ConcurrencyEntry = None })
        | ConcurrencyInput.Back -> editing session Page.Fields

    /// The next screen, and what the loop must do, for an input on the current one. An input that belongs to a screen
    /// that is not showing (a late result of a write, say) changes nothing. An interrupt leaves at once with no effect:
    /// nothing is written except by `Persist`, and a write is atomic.
    let update (state: EditorState) (input: Input) : EditorState * Effect list =
        match input, state with
        | Input.Interrupt, _ -> EditorState.Exited, []
        | Input.Menu menuInput, EditorState.MainMenu(profiles, _) -> menu profiles menuInput
        | Input.Picker pickerInput, EditorState.PickingDelete profiles -> picker profiles pickerInput
        | Input.Confirm confirmInput, EditorState.ConfirmingDelete(profiles, profile) -> confirm profiles profile confirmInput
        | Input.Fields fieldsInput, EditorState.Editing(session, Page.Fields) -> fields session fieldsInput
        | Input.Sources sourcesInput, EditorState.Editing(session, Page.Sources) -> sources session sourcesInput
        | Input.Source sourceInput, EditorState.Editing(session, Page.Source index) -> source session index sourceInput
        | Input.Entries entriesInput, EditorState.Editing(session, Page.Entries(index, list)) ->
            entries session index list entriesInput
        | Input.Retention retentionInput, EditorState.Editing(session, Page.Retention) -> retention session retentionInput
        | Input.Concurrency concurrencyInput, EditorState.Editing(session, Page.Concurrency) ->
            concurrency session concurrencyInput
        | Input.Persisted, EditorState.Persisting pending -> EditorState.MainMenu(pending.Candidate, Some pending.Done), []
        | Input.PersistFailed error, EditorState.Persisting pending -> attach (Notice.WriteFailed error) pending.Back, []
        | _ -> unchanged state
