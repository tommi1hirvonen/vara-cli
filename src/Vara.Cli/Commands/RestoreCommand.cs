using System.CommandLine;
using System.Globalization;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Core.Snapshots;

namespace Vara.Cli.Commands;

public static class RestoreCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory)
    {
        var profileArgument = new Argument<string>("profile") { Description = "The profile to restore from." };
        var pathArgument = new Argument<string>("path") { Description = "The relative path within the profile's mirror to restore." };
        var outOption = new Option<string>("--out") { Description = "Destination path to write the restored content to.", Required = true };
        var atOption = new Option<string?>("--at") { Description = "Restore the version current as of this date/time." };
        var versionOption = new Option<long?>("--version") { Description = "Restore this specific version id (see the 'history' command)." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var forceOption = new Option<bool>("--force") { Description = "Overwrite an existing file at --out without prompting for confirmation." };

        var command = new Command("restore", "Restore a file's historical content to a new location, without touching the live mirror.")
        {
            profileArgument, pathArgument, outOption, atOption, versionOption, configOption, forceOption,
        };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileArgument)!;
            var path = parseResult.GetValue(pathArgument)!;
            var outPath = parseResult.GetValue(outOption)!;
            var at = parseResult.GetValue(atOption);
            var version = parseResult.GetValue(versionOption);
            var configPath = parseResult.GetValue(configOption);
            var force = parseResult.GetValue(forceOption);

            if (at is null && version is null)
            {
                Console.Error.WriteLine("Error: specify either --at <date> or --version <id>.");
                return 1;
            }

            var cancelled = false;

            var exitCode = ErrorReporting.Run(() =>
            {
                var profile = profileResolver.Resolve(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

                void RunRestore(bool overwrite)
                {
                    if (version is not null)
                    {
                        history.RestoreVersion(path, version.Value, outPath, overwrite);
                    }
                    else
                    {
                        var asOf = DateTimeOffset.Parse(at!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
                        history.RestoreAsOf(path, asOf, outPath, overwrite);
                    }
                }

                try
                {
                    RunRestore(force);
                    Console.WriteLine($"Restored '{path}' to '{outPath}'.");
                }
                catch (DestinationExistsException) when (!force && !Console.IsInputRedirected)
                {
                    // Interactive session, no --force: ask before overwriting. The prompt goes to
                    // stderr so it's still visible even if stdout is redirected. Non-interactive
                    // sessions (stdin redirected) fall through and let the exception propagate to
                    // ErrorReporting, which reports it as a normal error (exit 1) directing the
                    // user to --force.
                    Console.Error.Write($"File '{outPath}' already exists. Overwrite? [y/N]: ");
                    var answer = Console.ReadLine();
                    if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                    {
                        RunRestore(overwrite: true);
                        Console.WriteLine($"Restored '{path}' to '{outPath}'.");
                    }
                    else
                    {
                        cancelled = true;
                    }
                }
            });

            if (cancelled)
            {
                Console.WriteLine("Restore cancelled: destination not overwritten.");
                return 0;
            }

            return exitCode;
        });

        return command;
    }
}

