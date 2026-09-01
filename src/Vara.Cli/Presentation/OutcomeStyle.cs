using Spectre.Console;

namespace Vara.Cli.Presentation;

/// <summary>
/// The four outcome-severity/neutral styles used consistently across every command's
/// reported outcome, per the <c>cli-presentation</c> capability's "Outcome severity is
/// visually distinct" requirement: a bold pastel green for a clean success, a bold
/// pastel amber for a success with partial failures, a bold pastel red for a hard
/// error, and a bold pastel blue for a neutral/in-progress state. Pastel tones (rather
/// than Spectre's default named <c>Green</c>/<c>Yellow</c>/<c>Red</c>) are used
/// throughout per design.md's "Color palette" decision, to read as softer than the
/// sharp, "neon" default shades while keeping the <c>Bold</c> decoration that
/// preserves legibility.
/// </summary>
public static class OutcomeStyle
{
    public static readonly Style Success = new(Color.PaleGreen1, decoration: Decoration.Bold);
    public static readonly Style PartialFailure = new(Color.LightGoldenrod2, decoration: Decoration.Bold);
    public static readonly Style Error = new(Color.IndianRed, decoration: Decoration.Bold);
    public static readonly Style Neutral = new(Color.LightSkyBlue1, decoration: Decoration.Bold);

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

    public static void WriteLineNeutral(IAnsiConsole console, string message) =>
        console.WriteLine(message, Neutral);
}
