using System.CommandLine;
using Spectre.Console;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

public static class DeletedCommand
{
    public static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        IAnsiConsole? console = null,
        IAnsiConsole? errorConsole = null)
    {
        var ansiConsole = console ?? AnsiConsole.Console;
        var profileOption = CommandOptions.Profile("query");
        var directoryArgument = new Argument<string?>("directory")
        {
            Description = "Scope the report to this directory - a mirror-relative path, an absolute source path, " +
                "relative to the current directory. Defaults to the whole profile.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var sinceOption = new Option<string?>("--since")
        {
            Description = "Only include files deleted at or after this date/time " +
                "(for example, '2025-01-15' or '2025-01-15 14:30')."
        };
        var configOption = CommandOptions.Config();

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
                using var session = HistorySession.Open(profileResolver, serviceFactory, profileName, configPath);

                var resolvedDirectory = string.IsNullOrWhiteSpace(directory)
                    ? null
                    : session.ResolveDirectory(directory).Path;
                var sinceDate = since is null ? (DateTimeOffset?)null : DateTimeOptionParser.Parse("--since", since);

                var deleted = session.History.ListDeleted(resolvedDirectory, sinceDate);
                DeletedReportPresenter.Render(ansiConsole, deleted);
            }, errorConsole);
        });

        return command;
    }
}
