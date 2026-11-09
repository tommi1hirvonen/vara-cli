/// `vara profiles [--config <path>]`: the interactive profile editor. The editor itself is the pure state machine in
/// `Vara.Domain` (`ProfileEditor.update`, `ProfileEditorView`); this is the thin loop around it. It shows the screen
/// after a clear, asks the screen's question, turns the answers into one input, calls `update`, and performs the effect
/// that comes back (a write of the configuration file) before it shows the next screen (design D1 and D4).
module Vara.Cli.ProfilesCommand

open System.CommandLine
open System.IO
open System.Threading
open System.Threading.Tasks
open Vara.Domain
open Vara.Engine

/// The exit code of an interrupt. The C# build leaves the process with success (`ProfilesCommand.RegisterCancellationExit`
/// passes `ExitCodes.Success`), and the spec only says the command "exits promptly".
let private interruptExitCode: int = Outcome.exitCode Outcome.Success

/// What the user is told when the session cannot be driven by a person. Worded as the C# build words it.
let private requiresTerminal = "'profiles' requires an interactive terminal."

/// The input a question gets from the user: a choice sends its input, or leads on to a further question.
[<TailCall>]
let rec private answer (prompts: Prompts) (question: Question) : Input =
    match question with
    | Question.Choose(title, choices) ->
        let index = prompts.Select title (choices |> List.map fst) 0

        match snd (List.item index choices) with
        | Answer.Send input -> input
        | Answer.Ask next -> answer prompts next
    | Question.Text(prompt, toInput) -> toInput (prompts.Text prompt)
    | Question.Confirm(text, yes, no) -> if prompts.Confirm text then yes else no

/// Feeds the input to the editor and performs what it asks, feeding each result back: a write is answered by
/// `Persisted` or `PersistFailed`, so a failed write leaves the screen and the draft where they were.
let rec private advance (env: CliEnv) (configPath: string) (state: EditorState) (input: Input) : EditorState =
    let next, effects = ProfileEditor.update state input

    effects
    |> List.fold
        (fun current effect ->
            match effect with
            | Effect.Persist profiles ->
                match env.Persist configPath profiles with
                | Ok() -> advance env configPath current Input.Persisted
                | Error error -> advance env configPath current (Input.PersistFailed error))
        next

/// One screen after another until the editor has exited. An answer that comes after a cancellation is the editor's own
/// `Interrupt`, so a draft the user was still editing is never written.
[<TailCall>]
let rec private loop (env: CliEnv) (configPath: string) (token: CancellationToken) (state: EditorState) : unit =
    match ProfileEditorView.ask state with
    | None -> ()
    | Some question ->
        // Cleared before every screen, so that no earlier copy of a menu, prompt or draft summary stays on screen.
        env.Alternate.Clear()
        HistorySession.show env (ProfileEditorView.view state)
        let input = answer env.Prompts question
        let input = if token.IsCancellationRequested then Input.Interrupt else input
        loop env configPath token (advance env configPath state input)

/// The profiles to start from: a configuration file that does not exist yet is no profiles, and the first save creates it.
let private startingProfiles (configPath: string) : Result<Profile list, ConfigError> =
    if File.Exists configPath then ConfigFile.load configPath else Ok []

/// Runs the editor on the alternate screen, and leaves the screen however the run ends.
let private session (env: CliEnv) (configPath: string) (token: CancellationToken) (profiles: Profile list) : unit =
    // A blocked prompt never sees the first Ctrl+C, which `GracefulCancellation` swallows. So the cancellation itself
    // restores the primary screen and ends the process. Nothing is written but by a `Persist`, which is atomic, so a draft
    // is never half saved, and a profile saved earlier is already on disk (design D5).
    use _interrupt =
        token.Register(fun () ->
            env.Alternate.Leave()
            env.Exit interruptExitCode)

    env.Alternate.Enter()

    try
        loop env configPath token (ProfileEditor.start profiles)
    finally
        env.Alternate.Leave()

let create (env: CliEnv) : Command =
    let configOption = HistorySession.configOption ()
    let command = Command("profiles", "Interactively create, edit, and delete backup profiles.")
    command.Options.Add configOption

    let handler (parseResult: ParseResult) (token: CancellationToken) : Task<Outcome> =
        task {
            let configPath = ConfigFile.locate (HistorySession.textOf parseResult configOption)

            // Checked before any prompt, so a redirected session fails with nothing half drawn and no file changed.
            if not (HistorySession.canPrompt env) then
                return ErrorReporting.usage env.ErrorConsole requiresTerminal
            else
                match startingProfiles configPath with
                | Error error -> return HistorySession.fail env (ConfigError.render error)
                | Ok profiles ->
                    session env configPath token profiles
                    return Outcome.Success
        }

    CommandAction.set env.ErrorConsole handler command
