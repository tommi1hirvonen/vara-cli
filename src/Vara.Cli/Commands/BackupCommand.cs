using System.CommandLine;
using Spectre.Console;
using Vara.Application.Backup;
using Vara.Application.Profiles;
using Vara.Application.Reporting;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.Abstractions;

namespace Vara.Cli.Commands;

public static class BackupCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory, IFileSystemScanner scanner, IHasher hasher)
    {
        var profileOption = new Option<string>("--profile") { Description = "The profile to back up.", Required = true };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var command = new Command("backup", "Run an incremental backup for a profile.") { profileOption, configOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption)!;
            var configPath = parseResult.GetValue(configOption);

            return ErrorReporting.Run(() =>
            {
                var profile = profileResolver.Resolve(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);

                var pipeline = new BackupPipeline(scanner, hasher, services.ContentStore, services.Repository, services.RunLock);
                var console = AnsiConsole.Console;

                var result = OutputMode.IsLiveCapable(console)
                    ? RunWithLiveDisplay(pipeline, profile, console)
                    : RunWithPlainOutput(pipeline, profile, console);

                console.WriteLine();
                BackupOutcomeReporter.Report(console, result);
            });
        });

        return command;
    }

    // Interactive path: a native Progress() run hosting three synthetic tasks (scan
    // spinner, then bar, then stats), all rendered by one BackupProgressColumn, per
    // design.md's "one unified column" decision. Progress()'s own AutoRefresh (unlike
    // Live, which has none) is what keeps throughput/ETA advancing during a stall - no
    // manual heartbeat timer is needed any more.
    private static BackupRunResult RunWithLiveDisplay(BackupPipeline pipeline, Vara.Core.Configuration.Profile profile, IAnsiConsole console)
    {
        var calculator = new BackupProgressCalculator();
        var displayGate = new ProgressDisplayGate();
        var column = new BackupProgressColumn(calculator);

        BackupRunResult result = null!;
        console.Progress()
            .Columns(column)
            .Start(ctx =>
            {
                var scanTask = ctx.AddTask("Scanning", autoStart: true);
                scanTask.IsIndeterminate = true;
                scanTask.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => BackupProgressRole.Scan);

                ProgressTask? barTask = null;
                ProgressTask? statsTask = null;

                void Render(BackupProgress admitted)
                {
                    if (barTask is null)
                    {
                        // First admitted progress event: the scan/plan phase has
                        // finished and transfer is about to begin - replace the scan
                        // spinner with the bar/stats rows, per the progress-reporting
                        // delta's "Indicator replaced once transfer begins" scenario.
                        ctx.RemoveTask(scanTask);
                        barTask = ctx.AddTask("Transfer", autoStart: true, maxValue: 100);
                        barTask.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => BackupProgressRole.Bar);
                        statsTask = ctx.AddTask("Stats", autoStart: true, maxValue: admitted.TotalBytes);
                        statsTask.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => BackupProgressRole.Stats);
                    }

                    // BackupProgressColumn.RenderBar drives the bar task's own
                    // Value/MaxValue itself (from this same state, via
                    // BackupProgressCalculator's percent) - it does not read raw
                    // bytes/total directly, so both tasks just get the raw admitted
                    // state stashed here.
                    var state = new BackupProgressState(admitted.BytesTransferred, admitted.TotalBytes);
                    barTask.State.Update(BackupProgressColumn.ProgressKey, (BackupProgressState _) => state);
                    statsTask!.Value = admitted.BytesTransferred;
                    statsTask.State.Update(BackupProgressColumn.ProgressKey, (BackupProgressState _) => state);

                    ctx.Refresh();
                }

                var progress = new Progress<BackupProgress>(p => displayGate.Report(p, Render));

                // The bar's final color reflects the run's actual outcome, not the
                // byte-based percentage BackupProgressColumn happened to reach - stamped
                // once here, after the run either returns or throws, per design.md's
                // "BackupCommand.RunWithLiveDisplay resolves and stamps the outcome
                // itself" decision. barTask is guaranteed non-null on a normal return
                // (onTransferPhaseStarting always fires before Run returns, even for a
                // zero-byte run); scanTask is still the active task if an exception is
                // thrown before transfer begins (e.g. during scanning/diffing/planning).
                try
                {
                    result = pipeline.Run(profile, progress);

                    var outcome = ResolveOutcome(result);
                    (barTask ?? scanTask).State.Update(BackupProgressColumn.OutcomeKey, (BackupProgressOutcome _) => outcome);
                    ctx.Refresh();
                }
                catch
                {
                    (barTask ?? scanTask).State.Update(BackupProgressColumn.OutcomeKey, (BackupProgressOutcome _) => BackupProgressOutcome.Error);
                    ctx.Refresh();
                    throw;
                }
            });

        return result;
    }

    /// <summary>
    /// Maps a completed run's result to the outcome the live progress bar's final
    /// color should reflect - a clean success (bar turns green) or a success with
    /// partial failures (bar turns/stays amber), per the progress-reporting delta's
    /// "Progress bar reflects run outcome severity" requirement. A hard error (the run
    /// throwing before completing) is handled separately, in <see cref="RunWithLiveDisplay"/>'s
    /// catch block, since no <see cref="BackupRunResult"/> exists in that case.
    /// Internal and pure so it can be unit tested directly, without needing a real
    /// <see cref="BackupPipeline"/>/<see cref="Progress"/> run.
    /// </summary>
    internal static BackupProgressOutcome ResolveOutcome(BackupRunResult result) =>
        result.FailedPaths.Count == 0 ? BackupProgressOutcome.Success : BackupProgressOutcome.PartialFailure;

    // Non-interactive/redirected path: plain, uncolored, appended lines with no
    // in-place redraw, per the cli-presentation delta's "Non-interactive or
    // color-incapable output falls back to plain text" requirement.
    private static BackupRunResult RunWithPlainOutput(BackupPipeline pipeline, Vara.Core.Configuration.Profile profile, IAnsiConsole console)
    {
        var calculator = new BackupProgressCalculator();
        var displayGate = new ProgressDisplayGate();

        void Render(BackupProgress admitted)
        {
            var snapshot = calculator.Calculate(admitted);
            var eta = snapshot.EstimatedTimeRemaining is { } remaining
                ? BackupRunSummaryFormatter.FormatDuration(remaining)
                : "calculating...";
            console.WriteLine(
                $"{BackupRunSummaryFormatter.FormatBytes(snapshot.BytesTransferred)} / {BackupRunSummaryFormatter.FormatBytes(snapshot.TotalBytes)} " +
                $"({snapshot.PercentComplete:0.0}%) - {BackupRunSummaryFormatter.FormatBytes((long)snapshot.ThroughputBytesPerSecond)}/s - ETA {eta}");
        }

        var progress = new Progress<BackupProgress>(p => displayGate.Report(p, Render));
        return pipeline.Run(profile, progress);
    }
}

