using System.CommandLine;
using Vara.Application.Backup;
using Vara.Application.Profiles;
using Vara.Application.Reporting;
using Vara.Cli.Composition;
using Vara.Core.Abstractions;

namespace Vara.Cli.Commands;

public static class BackupCommand
{
    // Comfortably larger than ProgressDisplayGate's own 100ms redraw interval, so a
    // heartbeat tick reliably passes the gate's interval check when due, rather than
    // frequently landing inside an already-rate-limited window (add-progress-heartbeat
    // change's design.md).
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
                var calculator = new BackupProgressCalculator();
                var displayGate = new ProgressDisplayGate();

                void Render(BackupProgress admitted)
                {
                    var snapshot = calculator.Calculate(admitted);
                    var eta = snapshot.EstimatedTimeRemaining is { } remaining
                        ? BackupRunSummaryFormatter.FormatDuration(remaining)
                        : "calculating...";
                    Console.Write(
                        $"\r{BackupRunSummaryFormatter.FormatBytes(snapshot.BytesTransferred)} / {BackupRunSummaryFormatter.FormatBytes(snapshot.TotalBytes)} " +
                        $"({snapshot.PercentComplete:0.0}%) - {BackupRunSummaryFormatter.FormatBytes((long)snapshot.ThroughputBytesPerSecond)}/s - ETA {eta}   ");
                }

                // The heartbeat re-presents the most recently observed progress value on a
                // fixed wall-clock interval, independent of the (possibly stalled) transfer
                // threads, so throughput/ETA keep reacting to elapsed time even when no new
                // byte-progress event has arrived for a while. Disposed once Run returns, so
                // no further ticks occur after the run completes.
                var heartbeat = new ProgressHeartbeat();
                var progress = new Progress<BackupProgress>(p =>
                {
                    heartbeat.Update(p);
                    displayGate.Report(p, Render);
                });

                BackupRunResult result;
                using (new Timer(_ => heartbeat.Tick(p => displayGate.Report(p, Render)), null, HeartbeatInterval, HeartbeatInterval))
                {
                    result = pipeline.Run(profile, progress);
                }

                Console.WriteLine();
                Console.WriteLine(BackupRunSummaryFormatter.Format(result));
            });
        });

        return command;
    }
}
