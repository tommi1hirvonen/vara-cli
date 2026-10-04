using System.CommandLine;
using Spectre.Console;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.FileSystem;

namespace Vara.Cli.Commands;

public static class SnapshotCommand
{
    public static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        IAnsiConsole? console = null,
        IAnsiConsole? errorConsole = null)
    {
        var ansiConsole = console ?? AnsiConsole.Console;
        var errorAnsiConsole = errorConsole ?? StandardError.Console;
        var snapshotArgument = new Argument<long>("id") { Description = "The snapshot id to inspect." };
        var directoryArgument = new Argument<string>("directory")
        {
            Description = "Optionally scope changes to a mirror-relative path, an absolute source path, or a path relative to the current directory.",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => ".",
        };
        var profileOption = new Option<string?>("--profile") { Description = "The profile to query. Optional when the current directory is inside a profile's target root." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var filesOption = new Option<bool>("--files") { Description = "Show changed file paths and change kinds in the directory tree." };

        var command = new Command("snapshot", "Show the directory changes recorded by one snapshot.")
        {
            snapshotArgument, directoryArgument, profileOption, configOption, filesOption,
        };

        command.SetAction(parseResult =>
        {
            var snapshotId = parseResult.GetValue(snapshotArgument);
            var directory = parseResult.GetValue(directoryArgument)!;
            var profileName = parseResult.GetValue(profileOption);
            var configPath = parseResult.GetValue(configOption);
            var includeFiles = parseResult.GetValue(filesOption);

            var unknownSnapshot = false;
            var exitCode = ErrorReporting.Run(() =>
            {
                var profile = profileResolver.ResolveForBrowsing(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile, createIfMissing: false);
                var resolution = DirectoryArgumentResolver.Resolve(
                    profile.TargetRoot,
                    directory,
                    services.Repository,
                    services.ContentStore.IsWithinMirror);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);
                var report = history.GetSnapshotChanges(snapshotId, resolution.Path, includeFiles);

                if (report is null)
                {
                    unknownSnapshot = true;
                    OutcomeStyle.WriteLineError(errorAnsiConsole, $"Error: snapshot #{snapshotId} is not recorded for profile '{profile.Name}'.");
                    return;
                }

                if (report.Directories.Count == 0)
                {
                    OutcomeStyle.WriteLineNeutral(ansiConsole, $"No changes recorded for snapshot #{snapshotId} in '{resolution.Path}'.");
                    return;
                }

                var sourceRoots = profile.Sources
                    .Where(source => !string.Equals(source.Path, profile.TargetRoot, StringComparison.OrdinalIgnoreCase))
                    .Select(source => AbsolutePathMirrorMapper.ToMirrorPath(source.Path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                SnapshotChangePresenter.Render(ansiConsole, report, sourceRoots);
            }, errorAnsiConsole);

            return unknownSnapshot ? ExitCodes.HardError : exitCode;
        });

        return command;
    }
}
