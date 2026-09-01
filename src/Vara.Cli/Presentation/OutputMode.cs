using Spectre.Console;

namespace Vara.Cli.Presentation;

/// <summary>
/// Decides whether the CLI can attempt a live, in-place-redrawn display (the backup
/// progress bar) or must fall back to plain, appended-line output, per the
/// `cli-presentation` capability's "Non-interactive or color-incapable output falls
/// back to plain text" requirement and the `progress-reporting` delta's "Terminal
/// width cannot be determined" scenario. A console whose output is not a real
/// terminal (for example, redirected to a file or pipe) has no meaningful width to
/// size a bar against, so no live display is attempted.
/// </summary>
public static class OutputMode
{
    public static bool IsLiveCapable(IAnsiConsole console) => console.Profile.Out.IsTerminal;
}
