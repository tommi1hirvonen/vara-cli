/// Tests of `vara profiles` through the command, with scripted prompts, a recording alternate screen and real
/// configuration files in a scratch directory. A test is named after a scenario of
/// openspec/specs/profile-management/spec.md in a module named after its requirement; the tests of the whole flows
/// have names of their own. The state machine itself is tested on plain data in the domain tests.
module Vara.Cli.Tests.ProfilesCommandTests

open System
open System.IO
open System.Threading
open Xunit
open Vara.Cli
open Vara.Cli.Tests.BackupCommandTests
open Vara.Domain
open Vara.Engine

/// One answer of the script, in the order the user would give it.
type Step =
    /// Picks the first choice whose text starts with this.
    | Pick of string
    | Type of string
    | Blank
    | Yes
    | No
    /// Does something between two prompts: a change of the world, or a Ctrl+C.
    | Do of (unit -> unit)

/// What the session did, in order: the screen calls, the questions, and the exit.
type Log = ResizeArray<string>

let private choiceTexts (choices: Span list list) : string list =
    choices |> List.map (fun choice -> choice |> List.map (fun (span: Span) -> span.Text) |> String.concat "")

/// Prompts that answer from the script and log each question. A question the script does not expect fails the test.
let private scripted (log: Log) (steps: Step list) : Prompts =
    let remaining = ref steps

    let rec next (question: string) : Step =
        match remaining.Value with
        | [] -> failwith $"the script has no step left for: {question}"
        | Do action :: rest ->
            remaining.Value <- rest
            action ()
            next question
        | step :: rest ->
            remaining.Value <- rest
            step

    { Select =
        fun title choices _ ->
            log.Add $"Select: {title}"
            let texts = choiceTexts choices

            match next title with
            | Pick start ->
                match texts |> List.tryFindIndex (fun text -> text.StartsWith start) with
                | Some index -> index
                | None ->
                    let listed = String.Join(" | ", texts)
                    failwith $"no choice starts with '{start}' in: {listed}"
            | other -> failwith $"expected a pick for '{title}', got {other}"
      Confirm =
        fun question ->
            log.Add $"Confirm: {question}"

            match next question with
            | Yes -> true
            | No -> false
            | other -> failwith $"expected yes or no for '{question}', got {other}"
      Text =
        fun prompt ->
            log.Add $"Text: {prompt}"

            match next prompt with
            | Type text -> Some text
            | Blank -> None
            | other -> failwith $"expected text for '{prompt}', got {other}" }

let private recordingScreen (log: Log) : LiveRenderer.AlternateScreen =
    { Enter = fun () -> log.Add "Enter"
      Clear = fun () -> log.Add "Clear"
      Leave = fun () -> log.Add "Leave" }

let private terminal: Capabilities =
    { IsTerminal = true
      SupportsColor = true
      NoColorRequested = false
      InputIsInteractive = true }

/// A session at an interactive terminal: scripted prompts, the recording screen, the real write of the file, and an
/// exit that is logged and does not end the test process.
let private interactive (log: Log) (steps: Step list) (env: CliEnv) : CliEnv =
    { env with
        Capabilities = terminal
        Prompts = scripted log steps
        Alternate = recordingScreen log
        Persist = ConfigFile.save
        Exit = fun code -> log.Add $"Exit {code}" }

let private profilesWith (customize: CliEnv -> CliEnv) (token: CancellationToken) (configPath: string) : Invocation =
    invokeEnv customize token [| "profiles"; "--config"; configPath |]

/// Runs the editor with the script on a configuration file; the invocation and what the session did.
let private run (configPath: string) (steps: Step list) : Invocation * Log =
    let log = Log()
    let result = profilesWith (interactive log steps) CancellationToken.None configPath
    result, log

let private docsConfig (setup: Scratch) : string = setup.Config

let private loaded (path: string) : Profile list =
    match ConfigFile.load path with
    | Ok profiles -> profiles
    | Error error -> failwith (ConfigError.render error)

let private names (profiles: Profile list) : string list = profiles |> List.map Profile.name

/// The screens of a log: the number of `Clear`s that come right before a question.
let private everyQuestionFollowsAClear (log: Log) : bool =
    log
    |> Seq.pairwise
    |> Seq.forall (fun (before, after) ->
        not (after.StartsWith "Select:" || after.StartsWith "Confirm:") || before = "Clear" || before.StartsWith "Text:")

/// Steps that add a valid profile: a name, a target and one source.
let private addProfile (name: string) (target: string) (source: string) : Step list =
    [ Pick "Add new profile"
      Pick "Edit name"
      Type name
      Pick "Edit target root"
      Type target
      Pick "Manage sources"
      Pick "Add source"
      Pick "Edit path"
      Type source
      Pick "Back"
      Pick "Back" ]

let private denyAccess (path: string) : unit = PruneCommandTests.denyAccess path
let private restoreAccess (path: string) : unit = PruneCommandTests.restoreAccess path

// ---- The command ----

module ``Interactive profiles command`` =
    [<Fact>]
    let ``Command run in an interactive terminal switches to alternate screen buffer`` () =
        scratch (fun setup ->
            let result, log = run (docsConfig setup) [ Pick "Quit" ]

            Assert.Equal(0, result.ExitCode)
            // The alternate screen is entered first, cleared, and the main menu is on it.
            Assert.Equal<string list>([ "Enter"; "Clear"; "Select: Vara profiles - select a profile to edit, or an action:"; "Leave" ], List.ofSeq log)
            Assert.Contains("docs", result.Output)
            Assert.Contains(setup.Target, result.Output))

    [<Fact>]
    let ``Command termination restores the primary screen buffer`` () =
        let endings: (string * Step list) list =
            [ "quit", [ Pick "Quit" ]
              "discard", [ Pick "Add new profile"; Pick "Discard"; Pick "Quit" ]
              "save",
              [ Pick "docs"; Pick "Edit name"; Type "documents"; Pick "Save"; Pick "Quit" ] ]

        for name, steps in endings do
            scratch (fun setup ->
                let result, log = run (docsConfig setup) steps
                Assert.Equal(0, result.ExitCode)
                // One pair, and the primary screen is back last, with nothing written to it afterwards.
                Assert.Equal(1, log |> Seq.filter ((=) "Enter") |> Seq.length)
                Assert.Equal("Enter", log[0])
                Assert.Equal("Leave", log[log.Count - 1])
                Assert.True(log |> Seq.filter ((=) "Leave") |> Seq.length = 1, name))

        // On an error: the prompt fails, the screen is left, and the failure is reported once it is on screen again.
        scratch (fun setup ->
            let log = Log()

            let broken (env: CliEnv) : CliEnv =
                { interactive log [] env with
                    Prompts = { (scripted log []) with Select = fun _ _ _ -> failwith "the terminal went away" } }

            let result = profilesWith broken CancellationToken.None (docsConfig setup)
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("the terminal went away", result.Errors)
            Assert.Equal<string list>([ "Enter"; "Clear"; "Leave" ], List.ofSeq log))

        // On an interrupt: the primary screen is restored first, then the process is left with success.
        scratch (fun setup ->
            let log = Log()
            use source = new CancellationTokenSource()

            let steps: Step list = [ Do(fun () -> source.Cancel()); Pick "Quit" ]
            let result = profilesWith (interactive log steps) source.Token (docsConfig setup)
            Assert.Equal(0, result.ExitCode)
            let leave = log.IndexOf "Leave"
            let exit = log.IndexOf "Exit 0"
            Assert.True(leave >= 0 && exit > leave, String.Join(", ", log)))

    [<Fact>]
    let ``Command run with input or output redirected`` () =
        scratch (fun setup ->
            let before = File.ReadAllText setup.Config

            let sessions: (string * (Capabilities -> Capabilities)) list =
                [ "input redirected", (fun capabilities -> { capabilities with InputIsInteractive = false })
                  "output redirected", (fun capabilities -> { capabilities with IsTerminal = false })
                  "both redirected", (fun _ -> { terminal with InputIsInteractive = false; IsTerminal = false }) ]

            for name, change in sessions do
                let log = Log()

                let env (env: CliEnv) : CliEnv =
                    { interactive log [] env with Capabilities = change terminal }

                let result = profilesWith env CancellationToken.None setup.Config
                Assert.True((result.ExitCode = 1), name)
                Assert.Contains("requires an interactive terminal", result.Errors)
                // Nothing was asked or drawn, and the file is as it was.
                Assert.Empty log
                Assert.Equal(before, File.ReadAllText setup.Config))

    [<Fact>]
    let ``Configuration file does not yet exist`` () =
        scratch (fun setup ->
            let missing = Path.Combine(setup.Root, "missing", "profiles.yml")
            let log = Log()

            let steps: Step list = [ Pick "Quit" ]
            let result = profilesWith (interactive log steps) CancellationToken.None missing

            Assert.Equal(0, result.ExitCode)
            Assert.Equal("", result.Errors)
            // The main menu with no profiles, offering "Add new profile".
            Assert.Contains("No profiles are configured yet.", result.Output)
            Assert.Equal<string list>([ "Enter"; "Clear"; "Select: Vara profiles - select a profile to edit, or an action:"; "Leave" ], List.ofSeq log)
            Assert.False(File.Exists missing)

            // The first save creates the file, and its directory.
            let created, _ = run missing (addProfile "docs" setup.Target setup.Source @ [ Pick "Save"; Pick "Quit" ])
            Assert.Equal(0, created.ExitCode)
            Assert.Equal<string list>([ "docs" ], names (loaded missing)))

// ---- Screens ----

module ``Profile fields are editable in any order`` =
    [<Fact>]
    let ``Screen re-display clears alternate screen without duplicate output`` () =
        scratch (fun setup ->
            let _, log =
                run (docsConfig setup) [ Pick "docs"; Pick "Edit name"; Type "a"; Pick "Edit target root"; Type "b"; Pick "Discard"; Pick "Quit" ]

            // Every screen was shown after a clear; a text prompt is part of the screen it was asked on.
            Assert.True(everyQuestionFollowsAClear log, String.Join(", ", log))
            Assert.Equal(5, log |> Seq.filter ((=) "Clear") |> Seq.length))

    [<Fact>]
    let ``Managing sources does not leave stale output on screen`` () =
        scratch (fun setup ->
            let _, log =
                run
                    (docsConfig setup)
                    [ Pick "docs"
                      Pick "Manage sources"
                      Pick "Add source"
                      Pick "Edit path"
                      Type @"C:\one"
                      Pick "Back"
                      Pick "Add source"
                      Pick "Back"
                      Pick "Back"
                      Pick "Discard"
                      Pick "Quit" ]

            // The main menu, the edit screen, the sources, a source, its path, the sources, a source, the sources, the edit
            // screen, the edit screen after the discard: each of these screens followed a clear.
            Assert.True(everyQuestionFollowsAClear log, String.Join(", ", log))

            let questions = log |> Seq.filter (fun entry -> entry.StartsWith "Select:") |> Seq.length
            Assert.Equal(questions, log |> Seq.filter ((=) "Clear") |> Seq.length))

// ---- Whole flows ----

module ``A profile is managed from start to finish`` =
    [<Fact>]
    let ``a profile is added through scripted prompts and saved to the file`` () =
        scratch (fun setup ->
            let created = Path.Combine(setup.Root, "elsewhere")
            let steps = addProfile "photos" created (Path.Combine(setup.Root, "pictures")) @ [ Pick "Save"; Pick "Quit" ]
            let result, _ = run (docsConfig setup) steps
            Assert.Equal(0, result.ExitCode)

            let saved = loaded setup.Config
            Assert.Equal<string list>([ "docs"; "photos" ], names saved)
            Assert.Contains("Saved profile 'photos'.", result.Output)
            let photos = saved |> List.find (fun profile -> Profile.name profile = "photos")
            Assert.Equal(created, AbsolutePath.value (Profile.target photos)))

    [<Fact>]
    let ``a profile is edited and its comments are not kept`` () =
        scratch (fun setup ->
            File.WriteAllText(
                setup.Config,
                $"# why\nprofiles:\n  - name: docs # main\n    target: {setup.Target}\n    sources:\n      - path: {setup.Source}\n"
            )

            let steps =
                [ Pick "docs"
                  Pick "Edit name"
                  Type "documents"
                  Pick "Retention"
                  Pick "Set keep_daily"
                  Type "5"
                  Pick "Back"
                  Pick "Concurrency"
                  Pick "Set scan_concurrency"
                  Type "3"
                  Pick "Back"
                  Pick "Save"
                  Pick "Quit" ]

            let result, _ = run setup.Config steps
            Assert.Equal(0, result.ExitCode)
            Assert.DoesNotContain("# why", File.ReadAllText setup.Config)

            match loaded setup.Config with
            | [ profile ] ->
                Assert.Equal("documents", Profile.name profile)
                Assert.Equal(Some 5, Profile.retention profile |> Option.map (fun policy -> RetentionCount.value policy.KeepDaily))
                Assert.Equal(Some 3, Profile.concurrency profile |> Option.bind _.Scan |> Option.map ConcurrencyLimit.value)
            | other -> failwith $"unexpected profiles {names other}")

    [<Fact>]
    let ``a profile is deleted after a confirmation and its backup data is not touched`` () =
        scratch (fun setup ->
            let data = Path.Combine(setup.Target, "kept.txt")
            Directory.CreateDirectory setup.Target |> ignore
            File.WriteAllText(data, "backup data")

            // Declined: nothing changes.
            let before = File.ReadAllText setup.Config
            let declined, declinedLog = run setup.Config [ Pick "Delete a profile"; Pick "docs"; No; Pick "Quit" ]
            Assert.Equal(0, declined.ExitCode)
            Assert.Equal(before, File.ReadAllText setup.Config)
            Assert.Contains(declinedLog, fun entry -> entry.StartsWith "Confirm: Delete profile 'docs'")

            // Confirmed: the entry is gone, the target is as it was.
            let confirmed, _ = run setup.Config [ Pick "Delete a profile"; Pick "docs"; Yes; Pick "Quit" ]
            Assert.Equal(0, confirmed.ExitCode)
            Assert.Empty(loaded setup.Config)
            Assert.Contains("Deleted profile 'docs'.", confirmed.Output)
            Assert.Equal("backup data", File.ReadAllText data)
            Assert.Equal<string list>([ "kept.txt" ], Directory.GetFileSystemEntries setup.Target |> Array.map Path.GetFileName |> List.ofArray))

    [<Fact>]
    let ``a draft that is discarded leaves the file as it was`` () =
        scratch (fun setup ->
            let before = File.ReadAllText setup.Config
            let result, _ = run setup.Config [ Pick "docs"; Pick "Edit name"; Type "changed"; Pick "Discard"; Pick "Add new profile"; Pick "Discard"; Pick "Quit" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal(before, File.ReadAllText setup.Config))

    [<Fact>]
    let ``a save that fails on a read-only file succeeds on retry`` () =
        scratch (fun setup ->
            let before = File.ReadAllText setup.Config
            File.SetAttributes(setup.Config, FileAttributes.ReadOnly)

            let steps =
                [ Pick "docs"
                  Pick "Edit name"
                  Type "documents"
                  Pick "Save"
                  // The save failed: the same draft is still on the edit screen. The cause is resolved, and it is saved again.
                  Do(fun () ->
                      Assert.Equal(before, File.ReadAllText setup.Config)
                      File.SetAttributes(setup.Config, FileAttributes.Normal))
                  Pick "Save"
                  Pick "Quit" ]

            let result, log = run setup.Config steps
            Assert.Equal(0, result.ExitCode)
            Assert.Contains($"Could not write profile configuration file '{setup.Config}'", result.Output)
            Assert.DoesNotContain("Unhandled", result.Output + result.Errors)
            Assert.DoesNotContain("   at ", result.Output + result.Errors)
            Assert.Equal<string list>([ "documents" ], names (loaded setup.Config))

            // The failure did not end the session: the edit screen was shown again, with no field typed a second time.
            Assert.Equal(1, log |> Seq.filter (fun entry -> entry.StartsWith "Text: Name") |> Seq.length))

    [<Fact>]
    let ``a delete that fails leaves the profile listed`` () =
        scratch (fun setup ->
            let before = File.ReadAllText setup.Config
            File.SetAttributes(setup.Config, FileAttributes.ReadOnly)

            try
                let result, _ = run setup.Config [ Pick "Delete a profile"; Pick "docs"; Yes; Pick "Quit" ]
                Assert.Equal(0, result.ExitCode)
                Assert.Contains("Could not write profile configuration file", result.Output)
                Assert.Equal(before, File.ReadAllText setup.Config)
            finally
                File.SetAttributes(setup.Config, FileAttributes.Normal))

    [<Fact>]
    let ``a save into a directory that cannot be written is reported, not thrown`` () =
        scratch (fun setup ->
            let directory = Path.Combine(setup.Root, "locked")
            Directory.CreateDirectory directory |> ignore
            let path = Path.Combine(directory, "profiles.yml")
            denyAccess directory

            try
                let steps = addProfile "docs" setup.Target setup.Source @ [ Pick "Save"; Pick "Discard"; Pick "Quit" ]
                let result, _ = run path steps
                Assert.Equal(0, result.ExitCode)
                Assert.Contains("Could not write profile configuration file", result.Output)
            finally
                restoreAccess directory

            Assert.False(File.Exists path))

// ---- Interrupt ----

module ``Interrupting the profiles editor exits without persisting`` =
    /// Saves one profile, opens another draft, and is interrupted with the answer the user was giving.
    let private interruptedAfter (setup: Scratch) (answer: Step) : Log * Invocation =
        let log = Log()
        use source = new CancellationTokenSource()

        let steps =
            addProfile "first" (Path.Combine(setup.Root, "t1")) (Path.Combine(setup.Root, "s1"))
            @ [ Pick "Save"
                Pick "Add new profile"
                Pick "Edit name"
                Type "second"
                Do(fun () -> source.Cancel())
                answer ]

        let result = profilesWith (interactive log steps) source.Token setup.Config
        log, result

    [<Fact>]
    let ``Interrupting while editing a draft`` () =
        scratch (fun setup ->
            let before = File.ReadAllText setup.Config
            let log = Log()
            use source = new CancellationTokenSource()

            // Ctrl+C at a prompt of a valid, unsaved draft: even if its answer were Save, nothing is written.
            let steps =
                addProfile "new" (Path.Combine(setup.Root, "t")) (Path.Combine(setup.Root, "s"))
                @ [ Do(fun () -> source.Cancel()); Pick "Save" ]

            let result = profilesWith (interactive log steps) source.Token setup.Config
            Assert.Equal(0, result.ExitCode)
            Assert.Equal(before, File.ReadAllText setup.Config)
            Assert.Contains("Exit 0", log)
            Assert.True(log.IndexOf "Leave" < log.IndexOf "Exit 0"))

    [<Fact>]
    let ``Interrupting after an earlier successful save`` () =
        scratch (fun setup ->
            let log, result = interruptedAfter setup (Pick "Save")
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Exit 0", log)
            // The first profile was saved; the abandoned draft was not written.
            Assert.Equal<string list>([ "docs"; "first" ], names (loaded setup.Config)))

    [<Fact>]
    let ``Interrupting at the main menu`` () =
        scratch (fun setup ->
            let before = File.ReadAllText setup.Config
            let log = Log()
            use source = new CancellationTokenSource()

            let steps: Step list = [ Do(fun () -> source.Cancel()); Pick "docs" ]
            let result = profilesWith (interactive log steps) source.Token setup.Config
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Exit 0", log)
            Assert.Equal(before, File.ReadAllText setup.Config)
            // The editor stopped at that prompt: no screen was shown after it.
            Assert.Equal("Leave", log[log.Count - 1]))

// ---- The effects ----

module ``The extended prompts and environment`` =
    [<Fact>]
    let ``a test replacing the prompts and the screen sees the calls in order`` () =
        scratch (fun setup ->
            let _, log = run setup.Config [ Pick "docs"; Pick "Edit name"; Type "x"; Pick "Discard"; Pick "Quit" ]

            Assert.Equal<string list>(
                [ "Enter"
                  "Clear"
                  "Select: Vara profiles - select a profile to edit, or an action:"
                  "Clear"
                  "Select: Choose a field to edit, or Save/Discard:"
                  "Text: Name (currently: docs; leave blank to clear):"
                  "Clear"
                  "Select: Choose a field to edit, or Save/Discard:"
                  "Clear"
                  "Select: Vara profiles - select a profile to edit, or an action:"
                  "Leave" ],
                List.ofSeq log
            ))

    [<Fact>]
    let ``a blank text entry clears the field`` () =
        scratch (fun setup ->
            let result, _ = run setup.Config [ Pick "docs"; Pick "Edit name"; Blank; Pick "Save"; Pick "Discard"; Pick "Quit" ]
            Assert.Equal(0, result.ExitCode)
            // Saving with no name is refused with the loader's message, and the file is as it was.
            Assert.Contains("missing required field 'name'", result.Output)
            Assert.Equal<string list>([ "docs" ], names (loaded setup.Config)))
