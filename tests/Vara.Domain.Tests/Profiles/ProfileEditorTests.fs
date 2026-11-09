/// Tests of the profile editor's draft, state machine and screens, on plain data: no console and no file. A test is named
/// after a scenario of openspec/specs/profile-management/spec.md and sits in a module named after its requirement.
module Vara.Domain.Tests.ProfileEditorTests

open System.Globalization
open Xunit
open Vara.Domain
open Vara.Domain.Tests.ProfileValidationTests

// ---- Fixtures ----

let private mapping = Node.mapping
let private scalar = Node.scalar

/// A saved profile, made by the loader's own function.
let profileOf (name: string) (target: string) (sources: string list) : Profile =
    match ProfileValidation.validateProfiles [ Node.profile name target (sources |> List.map Node.source) ] with
    | Ok [ profile ] -> profile
    | Ok _ -> failwith "expected one profile"
    | Error error -> failwith (ConfigError.render error)

let private docs = profileOf "docs" @"C:\backup\docs" [ @"C:\data\docs" ]
let private photos = profileOf "photos" @"C:\backup\photos" [ @"C:\data\photos"; @"C:\data\more" ]

let private sourceAt (path: string) : SourceDraft = { SourceDraft.empty with SourcePath = Some path }

let private draftOf (name: string) (target: string) (sources: string list) : ProfileDraft =
    { ProfileDraft.empty with
        EnteredName = Some name
        EnteredTarget = Some target
        DraftSources = sources |> List.map sourceAt }

/// Feeds the inputs in order; the states and the effects they asked for.
let run (state: EditorState) (inputs: Input list) : EditorState * Effect list =
    inputs
    |> List.fold
        (fun (current, effects) input ->
            let next, asked = ProfileEditor.update current input
            next, effects @ asked)
        (state, [])

let private fieldsInput (input: FieldsInput) : Input = Input.Fields input
let private menuInput (input: MenuInput) : Input = Input.Menu input
let private sourcesInput (input: SourcesInput) : Input = Input.Sources input
let private sourceInput (input: SourceInput) : Input = Input.Source input
let private entriesInput (input: EntriesInput) : Input = Input.Entries input

/// The editor on the edit screen of a draft among saved profiles.
let private editingDraft (saved: Profile list) (draft: ProfileDraft) : EditorState =
    EditorState.Editing({ Saved = saved; Draft = draft; Notice = None }, Page.Fields)

let private session (state: EditorState) : Session =
    match state with
    | EditorState.Editing(session, _) -> session
    | other -> failwith $"expected an edit screen, got {other}"

let private draftIn (state: EditorState) : ProfileDraft = (session state).Draft

let private text (display: Display) : string = String.concat "\n" (Display.plainLines display)
let private screen (state: EditorState) : string = text (ProfileEditorView.view state)

let private errorOf (result: Result<Profile, ConfigError>) : ConfigError =
    match result with
    | Error error -> error
    | Ok _ -> failwith "expected a validation error"

let private profileIn (result: Result<Profile, ConfigError>) : Profile =
    match result with
    | Ok profile -> profile
    | Error error -> failwith (ConfigError.render error)

/// What the loader says about the draft's tree on its own among the saved profiles' trees.
let private loaderSays (saved: Profile list) (draft: ProfileDraft) : Result<Profile list, ConfigError> =
    ProfileValidation.validateProfiles (ProfileNodes.ofProfiles saved @ [ ProfileDraft.toNode draft ])

let private names (profiles: Profile list) : string list = profiles |> List.map Profile.name

let private withCulture (name: string) (body: unit -> 'T) : 'T =
    let previous = CultureInfo.CurrentCulture

    try
        CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo name
        body ()
    finally
        CultureInfo.CurrentCulture <- previous

// ---- Draft validation ----

module ``Draft changes are validated live, after every field edit`` =
    [<Fact>]
    let ``Invalid field value rejected immediately`` () =
        let state = editingDraft [] (draftOf "p" @"C:ackup" [ @"C:\data" ])
        let inSources, _ = run state [ fieldsInput FieldsInput.ManageSources ]

        // The path is set to a relative one: the screen that follows shows the error, with no Save in between.
        let edited, effects =
            run inSources [ sourcesInput (SourcesInput.Open 0); sourceInput (SourceInput.SetPath(Some @"relative\path")) ]

        Assert.Empty effects
        Assert.Contains("Validation error:", screen edited)
        Assert.Contains("Source path 'relative\path' must be a fully-qualified, absolute path.", screen edited)

    [<Fact>]
    let ``Cross-field overlap detected live`` () =
        let state = editingDraft [] { draftOf "p" @"C:\backup" [ @"C:\data\docs" ] with EnteredTarget = None }
        let next, _ = run state [ fieldsInput (FieldsInput.SetTarget(Some @"C:\data")) ]
        Assert.Contains("Validation error:", screen next)
        Assert.Contains("overlaps source path", screen next)

    [<Fact>]
    let ``Duplicate name against another saved profile detected live`` () =
        let state = editingDraft [ docs ] (draftOf "other" @"C:\backup\other" [ @"C:\data\other" ])
        let next, _ = run state [ fieldsInput (FieldsInput.SetName(Some "docs")) ]
        Assert.Equal(ConfigError.DuplicateName "docs", errorOf (ProfileDraft.validate [ docs ] (draftIn next)))
        // The text names the saved profile and speaks of the profile being edited, not of the file.
        Assert.Contains("A profile named 'docs' already exists. Choose a different name to save this profile.", screen next)
        Assert.DoesNotContain("configuration file", screen next)

    [<Fact>]
    let ``Renaming a profile to its own current name is not a conflict`` () =
        let opened, _ = run (ProfileEditor.start [ docs; photos ]) [ menuInput (MenuInput.Open 0) ]
        let renamed, _ = run opened [ fieldsInput (FieldsInput.SetName(Some "docs")) ]
        Assert.Equal(docs, profileIn (ProfileDraft.validate [ docs; photos ] (draftIn renamed)))
        Assert.DoesNotContain("Validation error", screen renamed)

    [<Fact>]
    let ``Valid draft shows no error`` () =
        let state = editingDraft [ docs ] (draftOf "new" @"C:\backup\new" [ @"C:\data\new" ])
        Assert.Contains("Draft is valid.", screen state)
        Assert.DoesNotContain("Validation error", screen state)

    [<Fact>]
    let ``Adding an incomplete source immediately invalidates the draft`` () =
        let state = editingDraft [] (draftOf "p" @"C:\backup" [ @"C:\data" ])
        Assert.DoesNotContain("Validation error", screen state)

        let next, _ = run state [ fieldsInput FieldsInput.ManageSources; sourcesInput SourcesInput.Add; sourceInput SourceInput.Back; sourcesInput SourcesInput.Back ]

        Assert.Contains("Validation error: Profile 'p' is invalid: each source must define a 'path'", screen next)
        Assert.DoesNotContain("Draft is valid.", screen next)

    [<Fact>]
    let ``Numeric entry is interpreted independently of the machine's locale`` () =
        let draft =
            { draftOf "p" @"C:\backup" [ @"C:\data" ] with
                RetentionEntry = Some { RetentionDraft.empty with DailyText = "7"; YearlyText = "12" }
                ConcurrencyEntry = Some { ScanText = Some "4"; TransferText = Some "16" } }

        let invariant = ProfileDraft.validate [] draft

        for culture in [ "de-DE"; "fr-FR"; "sv-SE"; "ar-SA"; "tr-TR" ] do
            Assert.Equal(invariant, withCulture culture (fun () -> ProfileDraft.validate [] draft))

        // What the file says for the same counts is written without a culture.
        let profile = profileIn invariant
        let written = withCulture "ar-SA" (fun () -> ProfileNodes.ofProfile profile)
        Assert.Equal(ProfileNodes.ofProfile profile, written)
        Assert.Equal(Some 12, Profile.retention profile |> Option.map (fun policy -> RetentionCount.value policy.KeepYearly))

    [<Fact>]
    let ``The draft's validation is the loader's`` () =
        let drafts =
            [ draftOf "docs" @"C:\backup\x" [ @"C:\data\x" ]
              draftOf "x" @"C:\data" [ @"C:\data\x" ]
              draftOf "x" @"C:\backup\x" [ @"relative" ]
              { draftOf "x" @"C:\backup\x" [ @"C:\data\x" ] with EnteredName = None }
              { draftOf "x" @"C:\backup\x" [ @"C:\data\x" ] with RetentionEntry = Some { RetentionDraft.empty with DailyText = "-1" } }
              { draftOf "x" @"C:\backup\x" [ @"C:\data\x" ] with ConcurrencyEntry = Some { ScanText = Some "0"; TransferText = None } }
              draftOf "x" @"C:\backup\x" [] ]

        for draft in drafts do
            let expected = loaderSays [ docs ] draft |> Result.map List.last
            Assert.Equal(expected, ProfileDraft.validate [ docs ] draft)

// ---- Fields ----

module ``Profile fields are editable in any order`` =
    [<Fact>]
    let ``Editing fields in a non-sequential order`` () =
        let state, effects = run (ProfileEditor.start []) [ menuInput MenuInput.Add; fieldsInput (FieldsInput.SetTarget(Some @"C:\backup")) ]
        Assert.Empty effects
        Assert.Equal(Some @"C:\backup", (draftIn state).EnteredTarget)
        Assert.Equal(None, (draftIn state).EnteredName)
        Assert.Equal(Page.Fields, (match state with EditorState.Editing(_, page) -> page | _ -> failwith "not editing"))

        // The name can come later, and then the target is still there.
        let named, _ = run state [ fieldsInput (FieldsInput.SetName(Some "p")) ]
        Assert.Equal(Some @"C:\backup", (draftIn named).EnteredTarget)
        Assert.Equal(Some "p", (draftIn named).EnteredName)

    [<Fact>]
    let ``Unset fields are visibly distinguished`` () =
        let state = editingDraft [] ProfileDraft.empty
        let shown = screen state
        Assert.Contains("Name: (not set)", shown)
        Assert.Contains("Retention: not set", shown)
        Assert.Contains("Concurrency: not set", shown)

        // An unset value is in the detail tone and a value that is set is not.
        let tones (display: Display) : Tone list =
            let rec collect (lines: Line list) =
                lines
                |> List.collect (fun line ->
                    match line with
                    | Line.Box(_, inner) -> collect inner
                    | Line.Spans [ _; value ] -> [ value.Tone ]
                    | _ -> [])

            collect display

        Assert.Contains(Tone.Detail, tones (ProfileEditorView.view state))
        Assert.DoesNotContain(Tone.Detail, tones (ProfileEditorView.view (editingDraft [] (draftOf "p" @"C:\b" [ @"C:\a" ]))))

        let set, _ = run state [ fieldsInput (FieldsInput.SetName(Some "p")) ]
        Assert.Contains("Name: p", screen set)

    [<Fact>]
    let ``Screen re-display clears alternate screen without duplicate output`` () =
        // A screen is a function of the state alone: showing the state again, or after other edits that end in the same
        // draft, gives the same display, with one copy of the summary.
        let state, _ = run (ProfileEditor.start []) [ menuInput MenuInput.Add; fieldsInput (FieldsInput.SetName(Some "p")) ]
        let again, _ = run state [ fieldsInput (FieldsInput.SetName(Some "q")); fieldsInput (FieldsInput.SetName(Some "p")) ]
        Assert.Equal<Display>(ProfileEditorView.view state, ProfileEditorView.view again)

        let lines = Display.plainLines (ProfileEditorView.view again)
        Assert.Single(lines |> List.filter (fun line -> line.Contains "Name: ")) |> ignore
        Assert.Single(lines |> List.filter (fun line -> line.Contains "Target root: ")) |> ignore

    [<Fact>]
    let ``Managing sources does not leave stale output on screen`` () =
        let opened, _ = run (ProfileEditor.start []) [ menuInput MenuInput.Add; fieldsInput (FieldsInput.SetName(Some "p")) ]

        let after, _ =
            run
                opened
                [ fieldsInput FieldsInput.ManageSources
                  sourcesInput SourcesInput.Add
                  sourceInput (SourceInput.SetPath(Some @"C:\one"))
                  sourceInput SourceInput.Back
                  sourcesInput SourcesInput.Add
                  sourceInput (SourceInput.SetPath(Some @"C:\two"))
                  sourceInput SourceInput.Back
                  sourcesInput SourcesInput.Back ]

        // The edit screen again, listing both sources once and nothing of the source screens.
        let shown = Display.plainLines (ProfileEditorView.view after)
        Assert.Contains("Sources: 2", String.concat "\n" shown)
        Assert.DoesNotContain("Path: ", String.concat "\n" shown)
        Assert.Equal(1, shown |> List.filter (fun line -> line.Contains @"C:\one") |> List.length)
        Assert.Equal(1, shown |> List.filter (fun line -> line.Contains @"C:\two") |> List.length)

        // A source screen shows that source only.
        let onSource, _ = run after [ fieldsInput FieldsInput.ManageSources; sourcesInput (SourcesInput.Open 1) ]
        let sourceLines = Display.plainLines (ProfileEditorView.view onSource)
        Assert.Contains(@"Path: C:\two", sourceLines)
        Assert.DoesNotContain(sourceLines, fun line -> line.Contains @"C:\one")

// ---- Sources ----

module ``Sources are managed as an editable sub-list within a profile draft`` =
    [<Fact>]
    let ``Adding a source to a draft`` () =
        let state = editingDraft [] (draftOf "p" @"C:\backup" [])

        let next, effects =
            run state [ fieldsInput FieldsInput.ManageSources; sourcesInput SourcesInput.Add; sourceInput (SourceInput.SetPath(Some @"C:\data")) ]

        Assert.Empty effects
        Assert.Equal<SourceDraft list>([ sourceAt @"C:\data" ], (draftIn next).DraftSources)
        // The whole draft is validated, and it is valid now.
        Assert.Equal("p", Profile.name (profileIn (ProfileDraft.validate [] (draftIn next))))
        Assert.Contains("Draft is valid.", screen (fst (run next [ sourceInput SourceInput.Back; sourcesInput SourcesInput.Back ])))

    [<Fact>]
    let ``Editing an existing source's fields`` () =
        let state = editingDraft [] (draftOf "p" @"C:\backup" [ @"C:\data" ])

        let next, effects =
            run
                state
                [ fieldsInput FieldsInput.ManageSources
                  sourcesInput (SourcesInput.Open 0)
                  sourceInput (SourceInput.SetRecursive false)
                  sourceInput (SourceInput.OpenList StringList.Excludes)
                  entriesInput (EntriesInput.Add " tmp ")
                  entriesInput EntriesInput.Done
                  sourceInput (SourceInput.OpenList StringList.IncludeGlobs)
                  entriesInput (EntriesInput.Add "**/*.md")
                  entriesInput EntriesInput.Done
                  sourceInput (SourceInput.OpenList StringList.ExcludeGlobs)
                  entriesInput (EntriesInput.Add "*.tmp")
                  entriesInput (EntriesInput.Add "*.bak")
                  entriesInput (EntriesInput.Remove 0)
                  entriesInput EntriesInput.Done ]

        Assert.Empty effects

        let expected =
            { SourceDraft.empty with
                SourcePath = Some @"C:\data"
                IsRecursive = false
                ExcludeEntries = [ "tmp" ]
                IncludeGlobEntries = [ "**/*.md" ]
                ExcludeGlobEntries = [ "*.bak" ] }

        Assert.Equal<SourceDraft list>([ expected ], (draftIn next).DraftSources)
        let source = Profile.sources (profileIn (ProfileDraft.validate [] (draftIn next))) |> List.exactlyOne
        Assert.False source.Recursive
        Assert.Equal<string list>([ "tmp" ], source.Excludes)

    [<Fact>]
    let ``Removing a source from a draft`` () =
        let state = editingDraft [] (draftOf "p" @"C:\backup" [ @"C:\a"; @"C:\b" ])

        let next, effects =
            run state [ fieldsInput FieldsInput.ManageSources; sourcesInput (SourcesInput.Open 0); sourceInput SourceInput.Remove ]

        Assert.Empty effects
        Assert.Equal<SourceDraft list>([ sourceAt @"C:\b" ], (draftIn next).DraftSources)
        Assert.Contains("Draft is valid.", screen (fst (run next [ sourcesInput SourcesInput.Back ])))

    [<Fact>]
    let ``A source's own overlap re-evaluates against the current target`` () =
        let state = editingDraft [] { draftOf "p" @"C:\backup" [ @"C:\data\docs" ] with EnteredTarget = None }
        let before = ProfileDraft.validate [] (draftIn state)
        Assert.Equal(ConfigError.InvalidProfile("p", "missing required field 'target'"), errorOf before)

        let next, _ = run state [ fieldsInput (FieldsInput.SetTarget(Some @"C:\data")) ]

        match errorOf (ProfileDraft.validate [] (draftIn next)) with
        | ConfigError.InvalidProfile("p", reason) -> Assert.Contains("overlaps source path 'C:\\data\\docs'", reason)
        | other -> failwith $"unexpected {other}"

    [<Fact>]
    let ``A newly added source is part of the draft before its fields are filled in`` () =
        let state = editingDraft [] (draftOf "p" @"C:\backup" [ @"C:\data" ])
        let next, _ = run state [ fieldsInput FieldsInput.ManageSources; sourcesInput SourcesInput.Add; sourceInput SourceInput.Back; sourcesInput SourcesInput.Back ]
        Assert.Equal(2, List.length (draftIn next).DraftSources)
        Assert.Contains("Sources: 2", screen next)
        Assert.Contains("(not set)", screen next)
        Assert.Contains("each source must define a 'path'", screen next)

    [<Fact>]
    let ``Later source edits do not change an already-validated profile`` () =
        let state = editingDraft [] { draftOf "p" @"C:\backup" [ @"C:\data" ] with DraftSources = [ { sourceAt @"C:\data" with ExcludeEntries = [ "old" ] } ] }
        let validated = profileIn (ProfileDraft.validate [] (draftIn state))

        let next, _ =
            run
                state
                [ fieldsInput FieldsInput.ManageSources
                  sourcesInput (SourcesInput.Open 0)
                  sourceInput (SourceInput.OpenList StringList.Excludes)
                  entriesInput (EntriesInput.Add "new")
                  entriesInput (EntriesInput.Remove 0) ]

        Assert.Equal<string list>([ "old" ], (Profile.sources validated |> List.exactlyOne).Excludes)
        // The edit takes effect only through a validation of the draft.
        let revalidated = profileIn (ProfileDraft.validate [] (draftIn next))
        Assert.Equal<string list>([ "new" ], (Profile.sources revalidated |> List.exactlyOne).Excludes)

// ---- Main menu ----

module ``Main menu lists profiles and top-level actions`` =
    [<Fact>]
    let ``Profiles listed with summary information`` () =
        let shown = screen (ProfileEditor.start [ docs; photos ])
        Assert.Contains("docs", shown)
        Assert.Contains(@"C:\backup\docs", shown)
        Assert.Contains("photos", shown)

        // Each profile has its source count in its own row.
        let rows = Display.plainLines (ProfileEditorView.view (ProfileEditor.start [ docs; photos ]))
        Assert.Contains(rows, fun row -> row.Contains "photos" && row.Contains @"C:\backup\photos" && row.Contains "2")
        Assert.Contains(rows, fun row -> row.Contains "docs" && row.Contains @"C:\backup\docs" && row.Contains "1")

        // The choices are the profiles, then the actions.
        match ProfileEditorView.ask (ProfileEditor.start [ docs; photos ]) with
        | Some(Question.Choose(_, choices)) ->
            let labels = choices |> List.map (fun (spans, _) -> spans |> List.map _.Text |> String.concat "")
            Assert.Equal<string list>(
                [ @"docs - C:\backup\docs (1 source(s))"
                  @"photos - C:\backup\photos (2 source(s))"
                  "Add new profile"
                  "Delete a profile"
                  "Quit" ],
                labels
            )
        | _ -> failwith "expected a choice"

    [<Fact>]
    let ``Selecting an existing profile opens it for editing`` () =
        let state, effects = run (ProfileEditor.start [ docs; photos ]) [ menuInput (MenuInput.Open 1) ]
        Assert.Empty effects

        match state with
        | EditorState.Editing(session, Page.Fields) ->
            // Straight to the edit screen, with the saved values as the draft: no submenu in between.
            Assert.Equal(ProfileDraft.ofProfile photos, session.Draft)
            Assert.Equal<string list>([ "docs"; "photos" ], names session.Saved)
        | other -> failwith $"unexpected {other}"

    [<Fact>]
    let ``Selecting "Add new profile"`` () =
        let state, effects = run (ProfileEditor.start [ docs ]) [ menuInput MenuInput.Add ]
        Assert.Empty effects
        Assert.Equal(ProfileDraft.empty, draftIn state)
        Assert.Equal(None, (draftIn state).EnteredName)
        Assert.Empty (draftIn state).DraftSources
        Assert.Equal(None, (draftIn state).RetentionEntry)
        Assert.Equal(None, (draftIn state).ConcurrencyEntry)

    [<Fact>]
    let ``Quitting the main menu`` () =
        let state, effects = run (ProfileEditor.start [ docs ]) [ menuInput MenuInput.Quit ]
        Assert.Equal(EditorState.Exited, state)
        Assert.Empty effects

// ---- Delete ----

module ``Deleting a profile requires explicit confirmation`` =
    let private confirming: EditorState =
        fst (run (ProfileEditor.start [ docs; photos ]) [ menuInput MenuInput.StartDelete; Input.Picker(PickerInput.Pick 0) ])

    [<Fact>]
    let ``Confirming deletion removes the profile`` () =
        match ProfileEditorView.ask confirming with
        | Some(Question.Confirm(question, _, _)) ->
            // The question names the profile and its target root, and says that backup data stays.
            Assert.Contains("'docs'", question)
            Assert.Contains(@"C:\backup\docs", question)
            Assert.Contains("will not touch any backup data", question)
        | _ -> failwith "expected a confirmation"

        let waiting, effects = run confirming [ Input.Confirm ConfirmInput.Delete ]
        Assert.Equal<Effect list>([ Effect.Persist [ photos ] ], effects)

        let after, _ = run waiting [ Input.Persisted ]

        match after with
        | EditorState.MainMenu(profiles, Some(Notice.Deleted "docs")) -> Assert.Equal<string list>([ "photos" ], names profiles)
        | other -> failwith $"unexpected {other}"

        Assert.DoesNotContain("docs", (screen after).Replace("Deleted profile 'docs'.", ""))

    [<Fact>]
    let ``Declining deletion makes no change`` () =
        let after, effects = run confirming [ Input.Confirm ConfirmInput.Keep ]
        Assert.Empty effects
        Assert.Equal(EditorState.MainMenu([ docs; photos ], None), after)
        Assert.Contains("docs", screen after)

    [<Fact>]
    let ``Deleting a profile does not touch backup data`` () =
        // The only thing the editor can ask for is a write of the profile list: it has no effect that names a target.
        let _, effects = run confirming [ Input.Confirm ConfirmInput.Delete ]
        Assert.Equal<Effect list>([ Effect.Persist [ photos ] ], effects)

// ---- Save ----

module ``Save persists only a fully valid draft`` =
    [<Fact>]
    let ``Saving a valid new profile`` () =
        let state = editingDraft [ docs ] (draftOf "new" @"C:\backup\new" [ @"C:\data\new" ])
        let waiting, effects = run state [ fieldsInput FieldsInput.Save ]

        match effects with
        | [ Effect.Persist written ] ->
            Assert.Equal<string list>([ "docs"; "new" ], names written)
            let after, _ = run waiting [ Input.Persisted ]

            match after with
            | EditorState.MainMenu(profiles, Some(Notice.Saved "new")) -> Assert.Equal<string list>([ "docs"; "new" ], names profiles)
            | other -> failwith $"unexpected {other}"
        | other -> failwith $"unexpected {other}"

    [<Fact>]
    let ``Saving a valid edit to an existing profile`` () =
        let opened, _ = run (ProfileEditor.start [ docs; photos ]) [ menuInput (MenuInput.Open 0) ]
        let waiting, effects = run opened [ fieldsInput (FieldsInput.SetName(Some "documents")); fieldsInput FieldsInput.Save ]

        match effects with
        | [ Effect.Persist written ] ->
            // Replaced in place, found by the original name: the rename is not an addition.
            Assert.Equal<string list>([ "documents"; "photos" ], names written)
            Assert.Equal(photos, List.item 1 written)
            let after, _ = run waiting [ Input.Persisted ]

            match after with
            | EditorState.MainMenu(profiles, _) -> Assert.Equal<string list>([ "documents"; "photos" ], names profiles)
            | other -> failwith $"unexpected {other}"
        | other -> failwith $"unexpected {other}"

    [<Fact>]
    let ``Attempting to save an invalid draft`` () =
        let state = editingDraft [ docs ] (draftOf "docs" @"C:\backup\x" [ @"C:\data\x" ])
        let next, effects = run state [ fieldsInput FieldsInput.Save ]
        Assert.Empty effects
        Assert.Equal(Page.Fields, (match next with EditorState.Editing(_, page) -> page | _ -> failwith "left the edit screen"))
        Assert.Equal(Some(Notice.CannotSave(ConfigError.DuplicateName "docs")), (session next).Notice)
        Assert.Contains("Cannot save - validation error: A profile named 'docs' already exists.", screen next)
        Assert.Equal<Profile list>([ docs ], (session next).Saved)

    [<Fact>]
    let ``Saved content matches the draft on screen`` () =
        let draft =
            { draftOf "p" @"C:\backup\p" [ @"C:\data\a"; @"C:\data\b" ] with
                DraftSources = [ { sourceAt @"C:\data\a" with IsRecursive = false; ExcludeGlobEntries = [ "*.tmp" ] }; sourceAt @"C:\data\b" ]
                RetentionEntry = Some { RetentionDraft.empty with DailyText = "3" }
                ConcurrencyEntry = Some { ScanText = Some "2"; TransferText = None } }

        let _, effects = run (editingDraft [] draft) [ fieldsInput FieldsInput.Save ]

        match effects with
        | [ Effect.Persist [ written ] ] ->
            Assert.Equal("p", Profile.name written)
            Assert.Equal<string list>([ @"C:\data\a"; @"C:\data\b" ], Profile.sources written |> List.map (fun source -> AbsolutePath.value source.Path))
            Assert.False (Profile.sources written |> List.head).Recursive
            Assert.Equal<string list>([ "*.tmp" ], (Profile.sources written |> List.head).ExcludeGlobs)
            Assert.Equal(3, Profile.retention written |> Option.map (fun policy -> RetentionCount.value policy.KeepDaily) |> Option.defaultValue -1)
            Assert.Equal(Some 2, Profile.concurrency written |> Option.bind _.Scan |> Option.map ConcurrencyLimit.value)
        | other -> failwith $"unexpected {other}"

    [<Fact>]
    let ``Save refuses a draft that became invalid after its last displayed validation`` () =
        let state = editingDraft [] (draftOf "p" @"C:\backup" [ @"C:\data" ])
        Assert.Contains("Draft is valid.", screen state)

        // Edited into an invalid state; Save validates what the draft is now, and does not fall back to the valid one.
        let next, effects = run state [ fieldsInput (FieldsInput.SetTarget(Some @"C:\data\inside")); fieldsInput FieldsInput.Save ]
        Assert.Empty effects
        Assert.Contains("Cannot save - validation error:", screen next)
        Assert.Contains("overlaps source path", screen next)

// ---- Discard ----

module ``Discard abandons the draft without persisting`` =
    [<Fact>]
    let ``Discarding a new profile draft`` () =
        let state = editingDraft [ docs ] (draftOf "new" @"C:\backup\new" [ @"C:\data\new" ])
        let next, effects = run state [ fieldsInput FieldsInput.Discard ]
        Assert.Empty effects
        Assert.Equal(EditorState.MainMenu([ docs ], None), next)

    [<Fact>]
    let ``Discarding edits to an existing profile`` () =
        let opened, _ = run (ProfileEditor.start [ docs ]) [ menuInput (MenuInput.Open 0) ]

        let next, effects =
            run opened [ fieldsInput (FieldsInput.SetName(Some "changed")); fieldsInput (FieldsInput.SetTarget None); fieldsInput FieldsInput.Discard ]

        Assert.Empty effects
        Assert.Equal(EditorState.MainMenu([ docs ], None), next)

        // Opened again, the profile has the values it had before the edit.
        let again, _ = run next [ menuInput (MenuInput.Open 0) ]
        Assert.Equal(ProfileDraft.ofProfile docs, draftIn again)

    [<Fact>]
    let ``Discarding at any point is safe regardless of validity`` () =
        let state = editingDraft [ docs ] (draftOf "docs" @"C:\data" [ @"relative" ])
        Assert.Contains("Validation error", screen state)
        let next, effects = run state [ fieldsInput FieldsInput.Discard ]
        Assert.Empty effects
        Assert.Equal(EditorState.MainMenu([ docs ], None), next)

// ---- A failed write ----

module ``A failed configuration write is recoverable within the session`` =
    let private failure: ConfigWriteError =
        { ConfigPath = @"C:\Users\me\.vara\profiles.yml"
          Reason = "Access to the path is denied." }

    let private saving: EditorState * Effect list =
        run (editingDraft [ docs ] (draftOf "new" @"C:\backup\new" [ @"C:\data\new" ])) [ fieldsInput FieldsInput.Save ]

    [<Fact>]
    let ``Save fails because the configuration file cannot be written`` () =
        let waiting, _ = saving
        let next, effects = run waiting [ Input.PersistFailed failure ]
        Assert.Empty effects

        // Still on the edit screen, the draft as it was, the saved list as it was, the error naming file and reason.
        Assert.Equal(draftOf "new" @"C:\backup\new" [ @"C:\data\new" ], draftIn next)
        Assert.Equal<Profile list>([ docs ], (session next).Saved)
        Assert.Equal(Some(Notice.WriteFailed failure), (session next).Notice)
        Assert.Contains(@"C:\Users\me\.vara\profiles.yml", screen next)
        Assert.Contains("Access to the path is denied.", screen next)

    [<Fact>]
    let ``Retrying a save after the cause of the failure is resolved`` () =
        let waiting, _ = saving
        let failed, _ = run waiting [ Input.PersistFailed failure ]
        // Save again, with no field entered again: the same profile is written, and the editor goes to the main menu.
        let retrying, effects = run failed [ fieldsInput FieldsInput.Save ]
        Assert.Equal<string list>([ "docs"; "new" ], (match effects with [ Effect.Persist written ] -> names written | _ -> []))
        let after, _ = run retrying [ Input.Persisted ]

        match after with
        | EditorState.MainMenu(profiles, Some(Notice.Saved "new")) -> Assert.Equal<string list>([ "docs"; "new" ], names profiles)
        | other -> failwith $"unexpected {other}"

    [<Fact>]
    let ``Delete fails because the configuration file cannot be written`` () =
        let waiting, _ =
            run (ProfileEditor.start [ docs; photos ]) [ menuInput MenuInput.StartDelete; Input.Picker(PickerInput.Pick 0); Input.Confirm ConfirmInput.Delete ]

        let after, effects = run waiting [ Input.PersistFailed failure ]
        Assert.Empty effects

        match after with
        | EditorState.MainMenu(profiles, Some(Notice.WriteFailed error)) ->
            Assert.Equal<string list>([ "docs"; "photos" ], names profiles)
            Assert.Equal(failure, error)
        | other -> failwith $"unexpected {other}"

        Assert.Contains("docs", screen after)
        Assert.Contains(@"C:\Users\me\.vara\profiles.yml", screen after)

    [<Fact>]
    let ``A write failure is not reported as an internal failure`` () =
        let waiting, _ = saving
        let next, _ = run waiting [ Input.PersistFailed failure ]
        let shown = screen next
        Assert.Contains("Error: Could not write profile configuration file", shown)
        Assert.DoesNotContain("Unhandled", shown)
        Assert.DoesNotContain(" at ", shown)
        Assert.DoesNotContain("Exception", shown)

// ---- Interrupt ----

module ``Interrupting the profiles editor exits without persisting`` =
    [<Fact>]
    let ``Interrupting while editing a draft`` () =
        let state = editingDraft [ docs ] (draftOf "new" @"C:\backup\new" [ @"C:\data\new" ])
        let next, effects = run state [ Input.Interrupt ]
        Assert.Equal(EditorState.Exited, next)
        Assert.Empty effects

        // At a prompt of a sub-screen, and while a write is out, it is the same.
        for inner in [ fieldsInput FieldsInput.ManageSources; fieldsInput FieldsInput.EditRetention ] do
            let deeper, _ = run state [ inner ]
            Assert.Equal((EditorState.Exited, []), ProfileEditor.update deeper Input.Interrupt)

    [<Fact>]
    let ``Interrupting after an earlier successful save`` () =
        let saving, _ = run (editingDraft [] (draftOf "first" @"C:\backup\first" [ @"C:\data\first" ])) [ fieldsInput FieldsInput.Save ]
        let saved, _ = run saving [ Input.Persisted ]
        let next, effects = run saved [ menuInput MenuInput.Add; fieldsInput (FieldsInput.SetName(Some "second")); Input.Interrupt ]

        // The only write asked for was the first one; the abandoned draft asks for none.
        Assert.Equal(EditorState.Exited, next)
        Assert.Empty effects

    [<Fact>]
    let ``Interrupting at the main menu`` () =
        let next, effects = run (ProfileEditor.start [ docs ]) [ Input.Interrupt ]
        Assert.Equal(EditorState.Exited, next)
        Assert.Empty effects
