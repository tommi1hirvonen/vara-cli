using System.CommandLine;
using Spectre.Console;
using Vara.Application.Profiles;
using Vara.Application.Retention;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

public static class PruneCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory)
    {
        var profileArgument = new Argument<string>("profile") { Description = "The profile to prune." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var command = new Command("prune", "Apply a profile's retention policy: remove expired snapshots and unreferenced content.") { profileArgument, configOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileArgument)!;
            var configPath = parseResult.GetValue(configOption);

            return ErrorReporting.Run(() =>
            {
                var profile = profileResolver.Resolve(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);
                var pruneService = new PruneService(services.Repository, services.ContentStore, services.RunLock);

                var result = pruneService.Prune(profile);
                PruneOutcomeReporter.Report(AnsiConsole.Console, result);
            });
        });

        return command;
    }
}
