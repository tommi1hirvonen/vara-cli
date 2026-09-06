using System.CommandLine;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

public static class ShowCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory)
    {
        var profileOption = new Option<string?>("--profile") { Description = "The profile to query. Optional when the current directory is inside a profile's target root." };
        var pathArgument = new Argument<string>("path") { Description = "The file whose content to show - a mirror-relative path, an absolute source path, or a path relative to the current directory." };
        var atOption = new Option<string?>("--at") { Description = "Show the version current as of this date/time (for example, '2025-01-15' or '2025-01-15 14:30')." };
        var versionOption = new Option<long?>("--version") { Description = "Show this specific version id (see the 'history' command)." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };

        var command = new Command("show", "Stream a specific version's content to standard output, without writing it to disk.")
        {
            pathArgument, profileOption, atOption, versionOption, configOption,
        };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption);
            var path = parseResult.GetValue(pathArgument)!;
            var at = parseResult.GetValue(atOption);
            var version = parseResult.GetValue(versionOption);
            var configPath = parseResult.GetValue(configOption);

            if (at is null && version is null)
            {
                OutcomeStyle.WriteLineError(StandardError.Console, "Error: specify either --at <date> or --version <id>.");
                return 1;
            }

            return ErrorReporting.Run(() =>
            {
                var profile = profileResolver.ResolveForBrowsing(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

                var resolvedPath = SnapshotPathResolver.TryResolve(
                    profile.TargetRoot,
                    path,
                    candidate => services.Repository.GetFileHistory(candidate).Count > 0,
                    out var candidatePath)
                    ? candidatePath
                    : path;

                var asOf = at is null ? (DateTimeOffset?)null : DateTimeOptionParser.Parse("--at", at);

                using var stdout = Console.OpenStandardOutput();
                history.ShowVersion(resolvedPath, version, asOf, stdout);
            });
        });

        return command;
    }
}
