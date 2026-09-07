using System.Threading;

namespace Vara.Cli.Composition;

/// <summary>
/// Wires a single Ctrl+C into a cooperative cancellation signal a running backup can
/// observe, and a second Ctrl+C into an immediate forced exit - per backup-execution's
/// "Graceful cancellation via Ctrl+C" requirement. Kept as its own class (rather than
/// inline in <c>Program.cs</c>'s <c>Console.CancelKeyPress</c> registration) so both
/// the first-press and second-press code paths can be exercised directly in tests,
/// without raising a real console event.
/// </summary>
public sealed class GracefulCancellation(Action<int> forceExit)
{
    /// <summary>
    /// The cooperative "stop starting new work" signal threaded into
    /// <see cref="Vara.Application.Backup.BackupPipeline.Run"/>.
    /// </summary>
    public CancellationTokenSource TokenSource { get; } = new();

    /// <summary>
    /// Call from a <see cref="Console.CancelKeyPress"/> handler. On the first call,
    /// requests cooperative cancellation and returns <see langword="true"/> so the
    /// caller can suppress the OS's default immediate-termination behavior
    /// (<c>ConsoleCancelEventArgs.Cancel = true</c>). On a second call - meaning a
    /// graceful stop was already requested but has not yet finished draining - forces
    /// an immediate exit instead, mirroring the default behavior a first Ctrl+C
    /// suppressed, and returns <see langword="false"/> (irrelevant in practice, since
    /// <paramref name="forceExit"/> is not expected to return).
    /// </summary>
    public bool RequestCancellation()
    {
        if (TokenSource.IsCancellationRequested)
        {
            forceExit(ExitCodes.HardError);
            return false;
        }

        TokenSource.Cancel();
        return true;
    }
}
