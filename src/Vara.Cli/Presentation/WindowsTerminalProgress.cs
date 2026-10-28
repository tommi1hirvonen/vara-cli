using System.Globalization;
using Spectre.Console;

namespace Vara.Cli.Presentation;

internal sealed class WindowsTerminalProgress : IDisposable
{
    private const string IndeterminateSequence = "\x1b]9;4;3;0\x07";
    private const string ClearSequence = "\x1b]9;4;0;0\x07";

    private readonly TextWriter _writer;
    private readonly Lock _sync = new();
    private bool _closed;

    private WindowsTerminalProgress(TextWriter writer) => _writer = writer;

    public static WindowsTerminalProgress? TryCreate(IAnsiConsole console) =>
        ReferenceEquals(console, AnsiConsole.Console)
            ? TryCreate(console, Environment.GetEnvironmentVariable("WT_SESSION"))
            : null;

    internal static WindowsTerminalProgress? TryCreate(IAnsiConsole console, string? terminalSession)
    {
        if (string.IsNullOrWhiteSpace(terminalSession) || !OutputMode.IsLiveCapable(console))
        {
            return null;
        }

        return new WindowsTerminalProgress(console.Profile.Out.Writer);
    }

    public void SetIndeterminate() => Write(IndeterminateSequence);

    public void SetProgress(double percentComplete)
    {
        if (!double.IsFinite(percentComplete))
        {
            throw new ArgumentOutOfRangeException(nameof(percentComplete), "Progress must be a finite percentage.");
        }

        var percent = (int)Math.Round(Math.Clamp(percentComplete, 0, 100));
        Write($"\x1b]9;4;1;{percent.ToString(CultureInfo.InvariantCulture)}\x07");
    }

    public void SetProgress(long completed, long total) =>
        SetProgress(total > 0 ? (double)completed / total * 100 : 100);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _writer.Write(ClearSequence);
            _writer.Flush();
        }
    }

    private void Write(string sequence)
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _writer.Write(sequence);
            _writer.Flush();
        }
    }
}
