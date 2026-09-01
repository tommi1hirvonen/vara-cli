using Spectre.Console;

namespace Vara.Cli.Presentation;

/// <summary>
/// The three outcome-severity styles used consistently across every command's reported
/// outcome, per the <c>cli-presentation</c> capability's "Outcome severity is visually
/// distinct" requirement: a bold green for a clean success, a bold amber/yellow for a
/// success with partial failures, and a bold red for a hard error.
/// </summary>
public static class OutcomeStyle
{
    public static readonly Style Success = new(Color.Green, decoration: Decoration.Bold);
    public static readonly Style PartialFailure = new(Color.Yellow, decoration: Decoration.Bold);
    public static readonly Style Error = new(Color.Red, decoration: Decoration.Bold);

    // Plain Write/WriteLine (as opposed to Markup/MarkupLine) render the given text
    // literally rather than parsing it for markup tags, so arbitrary content (file
    // paths, profile names, exception messages) can never be misinterpreted as markup
    // syntax or throw a MarkupException.
    public static void WriteLineSuccess(IAnsiConsole console, string message) =>
        console.WriteLine(message, Success);

    public static void WriteLinePartialFailure(IAnsiConsole console, string message) =>
        console.WriteLine(message, PartialFailure);

    public static void WriteLineError(IAnsiConsole console, string message) =>
        console.WriteLine(message, Error);
}
