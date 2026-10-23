using Spectre.Console;
using Vara.Application.Backup;
using Vara.Application.Reporting;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

/// <summary>
/// Progress rendering shared by the single-file and recursive directory restores: a live
/// byte-progress bar on a terminal, or plain appended lines when output is redirected or
/// colour-incapable (per the cli-presentation spec's plain-text fallback requirement).
/// </summary>
internal static class RestoreProgressDisplay
{
    public static void Run(IAnsiConsole console, Func<int, string> buildLabel, Action<RestoreProgressReporter> restore) =>
        Run(console, buildLabel, reporter =>
        {
            restore(reporter);
            return true;
        });

    public static T Run<T>(IAnsiConsole console, Func<int, string> buildLabel, Func<RestoreProgressReporter, T> restore) =>
        OutputMode.IsLiveCapable(console)
            ? RunLive(console, buildLabel, restore)
            : RunPlain(console, restore);

    // A single task is created as soon as the total size is known (via
    // RestoreProgressReporter.OnSizeResolved, invoked right before extraction begins) so the
    // bar starts immediately at 0% against a known total - no separate scan/indeterminate
    // phase, per design.md. RemainingTimeColumn/TransferSpeedColumn compute ETA/throughput
    // natively from the task's own Value/MaxValue progression, so no hand-rolled
    // BackupProgressCalculator math is needed here.
    private static T RunLive<T>(IAnsiConsole console, Func<int, string> buildLabel, Func<RestoreProgressReporter, T> restore)
    {
        var displayGate = new ProgressDisplayGate();

        return LiveProgress.Run(
            console,
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new TransferSpeedColumn(),
            ],
            (ctx, terminalProgress) =>
            {
                ProgressTask? task = null;

                void Render(BackupProgress admitted)
                {
                    var percent = admitted.TotalBytes > 0
                        ? (double)admitted.BytesTransferred / admitted.TotalBytes * 100
                        : 100;
                    terminalProgress?.SetProgress(percent);
                    task ??= ctx.AddTask(
                        buildLabel(console.Profile.Width),
                        autoStart: true,
                        maxValue: admitted.TotalBytes);
                    task.Value = admitted.BytesTransferred;
                    ctx.Refresh();
                }

                return restore(new RestoreProgressReporter(displayGate, Render));
            });
    }

    private static T RunPlain<T>(IAnsiConsole console, Func<RestoreProgressReporter, T> restore)
    {
        var calculator = new BackupProgressCalculator();
        var displayGate = new ProgressDisplayGate();

        void Render(BackupProgress admitted)
        {
            var snapshot = calculator.Calculate(admitted);
            console.WriteLine(
                $"{BackupRunSummaryFormatter.FormatBytes(snapshot.BytesTransferred)} / " +
                $"{BackupRunSummaryFormatter.FormatBytes(snapshot.TotalBytes)} " +
                $"({snapshot.PercentComplete:0.0}%) - " +
                $"{BackupRunSummaryFormatter.FormatBytes((long)snapshot.ThroughputBytesPerSecond)}/s - " +
                $"ETA {BackupRunSummaryFormatter.FormatEta(snapshot.EstimatedTimeRemaining)}");
        }

        return restore(new RestoreProgressReporter(displayGate, Render));
    }
}
