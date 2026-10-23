using Spectre.Console;

namespace Vara.Cli.Presentation;

/// <summary>
/// Hosts a Spectre <c>Progress()</c> live display together with its Windows Terminal taskbar
/// progress, which every long-running command otherwise sets up by hand: the terminal
/// progress is created (and disposed) around the display, and the body's result is returned
/// instead of being smuggled out through a captured <c>null!</c> local.
/// </summary>
internal static class LiveProgress
{
    /// <param name="console">The console the display renders to.</param>
    /// <param name="columns">The progress columns, in display order.</param>
    /// <param name="body">
    /// Runs the work against the live <see cref="ProgressContext"/>; the second argument is
    /// the taskbar progress, or <see langword="null"/> when the host terminal has none.
    /// </param>
    /// <param name="startIndeterminate">
    /// Whether the taskbar progress starts in its indeterminate state, for commands whose
    /// first phase has no predictable duration.
    /// </param>
    public static T Run<T>(
        IAnsiConsole console,
        ProgressColumn[] columns,
        Func<ProgressContext, WindowsTerminalProgress?, T> body,
        bool startIndeterminate = false)
    {
        using var terminalProgress = WindowsTerminalProgress.TryCreate(console);
        if (startIndeterminate)
        {
            terminalProgress?.SetIndeterminate();
        }

        T result = default!;
        console.Progress()
            .Columns(columns)
            .Start(ctx => result = body(ctx, terminalProgress));
        return result;
    }
}
