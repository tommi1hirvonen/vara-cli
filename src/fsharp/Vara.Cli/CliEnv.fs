namespace Vara.Cli

open System
open System.IO
open Spectre.Console
open Vara.Domain
open Vara.Engine

/// The effects the commands run against, assembled once by the composition root. A test replaces any of them.
type CliEnv =
    { Console: IAnsiConsole
      /// What the console can do, read once: it decides between the live, plain and JSON output.
      Capabilities: Capabilities
      /// Standard error, where hard errors and warnings go.
      ErrorConsole: IAnsiConsole
      /// Where `--json` writes its one object. Bypasses the console, so nothing can wrap or style it.
      JsonOut: TextWriter
      /// How a command asks the user to choose or confirm. Standard error, so a prompt stays visible when standard
      /// output is redirected.
      Prompts: Prompts
      /// The directory the command runs in, read once: the flexible path forms are resolved against it.
      CurrentDirectory: string
      /// The offset of the local time zone at a local time, for a date given without one.
      UtcOffsetAt: DateTime -> TimeSpan
      /// Standard output as bytes, for `show`, which must stream binary content untouched.
      OpenStandardOutput: unit -> Stream
      /// Receives "has a stop been requested" and returns a backup run's effects.
      MakeBackupEnv: (unit -> bool) -> BackupEnv
      Power: PowerCalls
      /// The alternate screen of the profile editor: enter it, clear it before each screen, leave it.
      Alternate: LiveRenderer.AlternateScreen
      /// Writes the whole list of profiles to the configuration file, durably: the path, then the profiles.
      Persist: string -> Profile list -> Result<unit, ConfigWriteError>
      /// Ends the process with an exit code. The profile editor uses it when it is interrupted at a prompt, which only
      /// leaving the process can end.
      Exit: int -> unit
      /// The `WT_SESSION` value: set in Windows Terminal.
      TerminalSession: string | null }

module CliEnv =
    let live: CliEnv =
        { Console = AnsiConsole.Console
          Capabilities = Terminal.capabilitiesOf AnsiConsole.Console
          ErrorConsole = Terminal.standardError
          JsonOut = Console.Out
          Prompts = Prompts.onConsole Terminal.standardError
          CurrentDirectory = Environment.CurrentDirectory
          UtcOffsetAt = fun local -> TimeZoneInfo.Local.GetUtcOffset local
          OpenStandardOutput = fun () -> Console.OpenStandardOutput()
          MakeBackupEnv = BackupRun.live
          Power = PowerRequest.win32
          Alternate = LiveRenderer.alternateScreen AnsiConsole.Console
          Persist = ConfigFile.save
          Exit = Environment.Exit
          TerminalSession = Environment.GetEnvironmentVariable "WT_SESSION" }
