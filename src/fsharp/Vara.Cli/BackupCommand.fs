/// `vara backup <profile> [--config <path>] [--dry-run]`: resolves the profile, runs the engine, and prints an interim
/// summary styled by outcome. The profile is positional only; `--profile` is a syntax error.
module Vara.Cli.BackupCommand

// GetValue<bool> is declared to return a nullable type, which F# cannot express for a value type; the value is never null.
#nowarn "3265"

open System.CommandLine
open System.Threading
open System.Threading.Tasks
open Spectre.Console
open Vara.Domain
open Vara.Engine

/// Writes the first line in the outcome's style and the rest plainly, so the severity colours the headline only.
let private print (console: IAnsiConsole) (outcome: Outcome) (text: string) : unit =
    let headline, detail =
        match text.IndexOf '\n' with
        | -1 -> text, ""
        | index -> text.Substring(0, index).TrimEnd '\r', text.Substring(index + 1)

    Terminal.writeLine console (Terminal.styleOf outcome) headline

    if detail.Length > 0 then
        Terminal.writeLine console Style.Plain detail

let private planOutcome (summary: PlanSummary) : Outcome =
    if List.isEmpty summary.Unscanned then Outcome.Success else Outcome.PartialFailure

/// The command. `makeEnv` receives "has a stop been requested" and returns the run's effects; the composition root
/// passes `BackupRun.live`, a test passes its own.
let create (console: IAnsiConsole) (errorConsole: IAnsiConsole) (makeEnv: (unit -> bool) -> BackupEnv) : Command =
    let profileArgument = Argument<string>("profile", Description = "The profile to back up.")

    let configOption =
        Option<string | null>(
            "--config",
            Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)."
        )

    let dryRunOption =
        Option<bool>(
            "--dry-run",
            Description =
                "Preview the planned added/changed/moved/deleted changes and bytes to transfer without performing any writes."
        )

    let command = Command("backup", "Run an incremental backup for a profile.")
    command.Arguments.Add profileArgument
    command.Options.Add configOption
    command.Options.Add dryRunOption

    let handler (parseResult: ParseResult) (token: CancellationToken) : Task<Outcome> =
        task {
            let name = parseResult.GetValue profileArgument |> nonNull
            let configPath = ConfigFile.locate (parseResult.GetValue configOption |> Option.ofObj)

            match ProfileResolution.forProfile configPath name with
            | Error error -> return ErrorReporting.report errorConsole (ConfigError.render error)
            | Ok profile ->
                let env = makeEnv (fun () -> token.IsCancellationRequested)

                if parseResult.GetValue dryRunOption then
                    match BackupRun.dryRun env profile with
                    | Error refusal -> return ErrorReporting.report errorConsole (BackupRefusal.render refusal)
                    | Ok summary ->
                        let outcome = planOutcome summary
                        print console outcome (BackupSummary.formatPlan summary)
                        return outcome
                else
                    match! BackupRun.run env profile with
                    | Error refusal -> return ErrorReporting.report errorConsole (BackupRefusal.render refusal)
                    | Ok result ->
                        let outcome = Run.outcome result
                        print console outcome (BackupSummary.formatRun result)
                        return outcome
        }

    CommandAction.set errorConsole handler command
