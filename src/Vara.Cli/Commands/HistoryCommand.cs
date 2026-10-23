using System.CommandLine;
using Spectre.Console;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

public static class HistoryCommand
{
    public static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        IAnsiConsole? console = null,
        IAnsiConsole? errorConsole = null)
    {
        var ansiConsole = console ?? AnsiConsole.Console;
        var profileOption = CommandOptions.Profile("query");
        var pathArgument = new Argument<string>("path")
        {
            Description = "The file to show history for - a mirror-relative path, an absolute source path, or a path " +
                "relative to the current directory."
        };
        var configOption = CommandOptions.Config();
        var command = new Command("history", "List the recorded versions of a file, most recent first.")
        {
            pathArgument, profileOption, configOption
        };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption);
            var path = parseResult.GetValue(pathArgument)!;
            var configPath = parseResult.GetValue(configOption);

            return ErrorReporting.Run(() =>
            {
                using var session = HistorySession.Open(profileResolver, serviceFactory, profileName, configPath);
                var versions = session.History.GetFileHistory(session.ResolveFilePath(path));
                HistoryTablePresenter.Render(ansiConsole, versions);
            }, errorConsole);
        });

        return command;
    }
}
