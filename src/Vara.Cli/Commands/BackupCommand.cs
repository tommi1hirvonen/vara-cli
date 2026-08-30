using System.CommandLine;
using Vara.Application.Backup;
using Vara.Application.Profiles;
using Vara.Application.Reporting;
using Vara.Cli.Composition;
using Vara.Core.Abstractions;

namespace Vara.Cli.Commands;

public static class BackupCommand
{
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
                var progress = new Progress<BackupProgress>(p =>
                {
                    var snapshot = calculator.Calculate(p);
                    var eta = snapshot.EstimatedTimeRemaining is { } remaining
                        ? BackupRunSummaryFormatter.FormatDuration(remaining)
                        : "calculating...";
                    Console.Write(
                        $"\r{BackupRunSummaryFormatter.FormatBytes(snapshot.BytesTransferred)} / {BackupRunSummaryFormatter.FormatBytes(snapshot.TotalBytes)} " +
                        $"({snapshot.PercentComplete:0.0}%) - {BackupRunSummaryFormatter.FormatBytes((long)snapshot.ThroughputBytesPerSecond)}/s - ETA {eta}   ");
                });

                var result = pipeline.Run(profile, progress);

                Console.WriteLine();
                Console.WriteLine(BackupRunSummaryFormatter.Format(result));
            });
        });

        return command;
    }
}
