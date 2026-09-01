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
    // The heartbeat's redraw cadence: comfortably larger than ProgressDisplayGate's own
    // 100ms rate-limit interval, so a heartbeat tick reliably passes the gate's interval
    // check when due, rather than frequently landing inside an already-rate-limited
    // window (add-progress-heartbeat change's design.md).
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(500);

    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory, IFileSystemScanner scanner, IHasher hasher)
    {
        var profileArgument = new Argument<string>("profile") { Description = "The profile to back up." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var command = new Command("backup", "Run an incremental backup for a profile.") { profileArgument, configOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileArgument)!;
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

    // Interactive path: a two-row Live display (bar on its own line, stats below it),
    // per the progress-reporting delta's "Progress bar occupies a dedicated,
    // width-sized line" requirement.
    private static BackupRunResult RunWithLiveDisplay(BackupPipeline pipeline, Vara.Core.Configuration.Profile profile, IAnsiConsole console)
    {
        var calculator = new BackupProgressCalculator();
        var displayGate = new ProgressDisplayGate();
        var panel = new BackupProgressPanel(calculator);

        BackupRunResult result = null!;
        console.Live(panel).Start(ctx =>
        {
            void Render(BackupProgress admitted)
            {
                panel.Update(admitted);
                ctx.Refresh();
            }

            var progress = new Progress<BackupProgress>(p => displayGate.Report(p, Render));

            // A minimal heartbeat: LiveDisplay has no built-in auto-refresh of its own
            // (confirmed by spike - see design.md), so this timer is what keeps
            // throughput/ETA advancing during a stall. Unlike the old ProgressHeartbeat,
            // it does not need to remember or replay a snapshot value - BackupProgressPanel
            // already recomputes elapsed-time-based values fresh on every Render() call.
            using var heartbeatTimer = new Timer(_ => ctx.Refresh(), null, HeartbeatInterval, HeartbeatInterval);
            result = pipeline.Run(profile, progress);
        });

        return result;
    }

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

