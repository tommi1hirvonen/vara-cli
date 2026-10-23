using System.CommandLine;
using Spectre.Console;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

public static class SnapshotsCommand
{
    public static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        IAnsiConsole? console = null,
        IAnsiConsole? errorConsole = null)
    {
        var ansiConsole = console ?? AnsiConsole.Console;
        var profileArgument = new Argument<string?>("profile")
        {
            Description = "The profile whose snapshots to list. Optional when the current directory is inside a " +
                "profile's target root.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var configOption = CommandOptions.Config();
        var command = new Command("snapshots", "List recorded snapshots for a profile.")
        {
            profileArgument, configOption
        };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileArgument);
            var configPath = parseResult.GetValue(configOption);

            return ErrorReporting.Run(() =>
            {
                using var session = HistorySession.Open(profileResolver, serviceFactory, profileName, configPath);
                SnapshotsTablePresenter.Render(ansiConsole, session.History.ListSnapshots());
            }, errorConsole);
        });

        return command;
    }
}
