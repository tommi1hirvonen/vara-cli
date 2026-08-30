using System.CommandLine;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Application.Reporting;
using Vara.Cli.Composition;

namespace Vara.Cli.Commands;

public static class SnapshotsCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory)
    {
        var profileArgument = new Argument<string>("profile") { Description = "The profile whose snapshots to list." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var command = new Command("snapshots", "List recorded snapshots for a profile.") { profileArgument, configOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileArgument)!;
            var configPath = parseResult.GetValue(configOption);

            return ErrorReporting.Run(() =>
            {
                var profile = profileResolver.Resolve(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

                var snapshots = history.ListSnapshots();
                if (snapshots.Count == 0)
                {
                    Console.WriteLine("No snapshots recorded yet.");
                    return;
                }

                foreach (var snapshot in snapshots)
                {
                    Console.WriteLine(
                        $"#{snapshot.Id,-6} {snapshot.StartedAt:yyyy-MM-dd HH:mm:ss zzz} [{snapshot.Status}] " +
                        $"+{snapshot.Stats.FilesAdded} ~{snapshot.Stats.FilesChanged} ->{snapshot.Stats.FilesMoved} -{snapshot.Stats.FilesDeleted} " +
                        $"{BackupRunSummaryFormatter.FormatBytes(snapshot.Stats.BytesTransferred)}");
                }
            });
        });

        return command;
    }
}
