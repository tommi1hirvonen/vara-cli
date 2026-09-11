using System.CommandLine;
using Spectre.Console;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

public static class DeletedCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory)
    {
        var profileOption = new Option<string?>("--profile") { Description = "The profile to query. Optional when the current directory is inside a profile's target root." };
        var directoryArgument = new Argument<string?>("directory")
        {
            Description = "Scope the report to this directory - a mirror-relative path, an absolute source path, or a path relative to the current directory. Defaults to the whole profile.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var sinceOption = new Option<string?>("--since") { Description = "Only include files deleted at or after this date/time (for example, '2025-01-15' or '2025-01-15 14:30')." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };

        var command = new Command("deleted", "Report files deleted from a profile, most recently deleted first.")
        {
            directoryArgument, profileOption, sinceOption, configOption,
        };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption);
            var directory = parseResult.GetValue(directoryArgument);
            var since = parseResult.GetValue(sinceOption);
            var configPath = parseResult.GetValue(configOption);

            return ErrorReporting.Run(() =>
            {
                var profile = profileResolver.ResolveForBrowsing(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile, createIfMissing: false);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

                var resolvedDirectory = string.IsNullOrWhiteSpace(directory)
                    ? null
                    : DirectoryArgumentResolver.Resolve(profile.TargetRoot, directory, services.Repository, services.ContentStore.IsWithinMirror).Path;
                var sinceDate = since is null ? (DateTimeOffset?)null : DateTimeOptionParser.Parse("--since", since);

                var deleted = history.ListDeleted(resolvedDirectory, sinceDate);
                DeletedReportPresenter.Render(AnsiConsole.Console, deleted);
            });
        });

        return command;
    }
}
