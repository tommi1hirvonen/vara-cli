using System.CommandLine;
using Spectre.Console;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

public static class BrowseCommand
{
    public static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        IAnsiConsole? console = null,
        IAnsiConsole? errorConsole = null)
    {
        // Overridable so tests can assert against an in-memory console instead of the
        // process-wide AnsiConsole.Console static (which would otherwise race other tests'
        // console output under the test suite's default parallelization) - the same
        // testability seam BackupCommand.Create's own console parameter uses.
        var ansiConsole = console ?? AnsiConsole.Console;

        var profileOption = CommandOptions.Profile("browse");
        var directoryArgument = new Argument<string>("directory")
        {
            Description = "The directory to list - a mirror-relative path, an absolute source path, or " +
                "a path relative to the current directory. Defaults to the mirror root.",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => ".",
        };
        var atOption = new Option<string?>("--at")
        {
            Description = "List the directory's contents as of this date/time instead of now " +
                "(for example, '2025-01-15' or '2025-01-15 14:30')."
        };
        var deletedOption = new Option<bool>("--deleted")
        {
            Description = "Include deleted entries, interleaved among live ones and visually marked."
        };
        var configOption = CommandOptions.Config();
        var command = new Command("browse", "List the immediate (one-level) contents of a directory within a profile's mirror.")
        {
            directoryArgument, profileOption, atOption, deletedOption, configOption,
        };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption);
            var directory = parseResult.GetValue(directoryArgument)!;
            var at = parseResult.GetValue(atOption);
            var includeDeleted = parseResult.GetValue(deletedOption);
            var configPath = parseResult.GetValue(configOption);

            return ErrorReporting.Run(() =>
            {
                using var session = HistorySession.Open(profileResolver, serviceFactory, profileName, configPath);

                var resolution = session.ResolveDirectory(directory);
                var asOf = at is null ? (DateTimeOffset?)null : DateTimeOptionParser.Parse("--at", at);

                var entries = session.History.ListDirectory(resolution.Path, asOf, includeDeleted);
                DirectoryListingPresenter.Render(ansiConsole, entries);

                if (resolution.FellBackToMirrorRoot)
                {
                    OutcomeStyle.WriteLineNeutral(
                        ansiConsole, "The current directory has no recorded history; showing the mirror root instead.");
                }
            }, errorConsole);
        });

        return command;
    }
}
