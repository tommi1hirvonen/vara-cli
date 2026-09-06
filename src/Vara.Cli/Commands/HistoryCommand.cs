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
        var profileOption = new Option<string?>("--profile") { Description = "The profile to query. Optional when the current directory is inside a profile's target root." };
        var pathArgument = new Argument<string>("path") { Description = "The file to show history for - a mirror-relative path, an absolute source path, or a path relative to the current directory." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var command = new Command("history", "List the recorded versions of a file, most recent first.") { pathArgument, profileOption, configOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption);
            var path = parseResult.GetValue(pathArgument)!;
            var configPath = parseResult.GetValue(configOption);

            return ErrorReporting.Run(() =>
            {
                var profile = profileResolver.ResolveForBrowsing(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile, createIfMissing: false);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

                var resolvedPath = SnapshotPathResolver.TryResolve(
                    profile.TargetRoot,
                    path,
                    candidate => services.Repository.GetFileHistory(candidate).Count > 0,
                    out var candidatePath)
                    ? candidatePath
                    : path;

                var versions = history.GetFileHistory(resolvedPath);
                HistoryTablePresenter.Render(AnsiConsole.Console, versions);
            });
        });

        return command;
    }
}

