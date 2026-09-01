using Spectre.Console;

namespace Vara.Cli.Presentation;

/// <summary>
/// A Spectre.Console-backed console instance bound to standard error, for output that
/// must remain visible even when standard output is redirected: hard-error messages
/// (<see cref="Vara.Cli.Composition.ErrorReporting"/>) and the restore overwrite
/// confirmation prompt.
/// </summary>
public static class StandardError
{
    public static readonly IAnsiConsole Console = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(System.Console.Error),
    });
}
