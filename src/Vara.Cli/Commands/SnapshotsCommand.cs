using System.CommandLine;
using Spectre.Console;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

public static class SnapshotsCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory)
    {
        var profileOption = new Option<string?>("--profile") { Description = "The profile whose snapshots to list. Optional when the current directory is inside a profile's target root." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var command = new Command("snapshots", "List recorded snapshots for a profile.") { profileOption, configOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption);
            var configPath = parseResult.GetValue(configOption);

            return ErrorReporting.Run(() =>
            {
                var profile = profileResolver.ResolveForBrowsing(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile, createIfMissing: false);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

                var snapshots = history.ListSnapshots();
                SnapshotsTablePresenter.Render(AnsiConsole.Console, snapshots);
            });
        });

        return command;
    }
}
