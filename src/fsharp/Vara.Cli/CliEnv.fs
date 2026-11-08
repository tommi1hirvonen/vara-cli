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
      /// Receives "has a stop been requested" and returns a backup run's effects.
      MakeBackupEnv: (unit -> bool) -> BackupEnv
      Power: PowerCalls
      /// The `WT_SESSION` value: set in Windows Terminal.
      TerminalSession: string | null }

module CliEnv =
    let live: CliEnv =
        { Console = AnsiConsole.Console
          Capabilities = Terminal.capabilitiesOf AnsiConsole.Console
          ErrorConsole = Terminal.standardError
          JsonOut = Console.Out
          MakeBackupEnv = BackupRun.live
          Power = PowerRequest.win32
          TerminalSession = Environment.GetEnvironmentVariable "WT_SESSION" }
