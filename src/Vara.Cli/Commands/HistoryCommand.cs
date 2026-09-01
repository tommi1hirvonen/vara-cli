using System.CommandLine;
using Spectre.Console;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

public static class HistoryCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory)
    {
        var profileArgument = new Argument<string>("profile") { Description = "The profile to query." };
        var pathArgument = new Argument<string>("path") { Description = "The relative path within the profile's mirror to show history for." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var command = new Command("history", "List the recorded versions of a file, most recent first.") { profileArgument, pathArgument, configOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileArgument)!;
            var path = parseResult.GetValue(pathArgument)!;
            var configPath = parseResult.GetValue(configOption);

            return ErrorReporting.Run(() =>
            {
                var profile = profileResolver.Resolve(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

                var versions = history.GetFileHistory(path);
                HistoryTablePresenter.Render(AnsiConsole.Console, versions);
            });
        });

        return command;
    }
}
