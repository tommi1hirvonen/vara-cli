using System.CommandLine;
using Spectre.Console;
using Vara.Application.Profiles;
using Vara.Application.Retention;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.Backup;

namespace Vara.Cli.Commands;

public static class PruneCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory)
    {
        var profileArgument = new Argument<string>("profile") { Description = "The profile to prune." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var yesOption = new Option<bool>("--yes", "-y") { Description = "Confirm the run without prompting, even if snapshots are eligible for removal." };
        var command = new Command("prune", "Apply a profile's retention policy: remove expired snapshots and unreferenced content.") { profileArgument, configOption, yesOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileArgument)!;
            var configPath = parseResult.GetValue(configOption);
            var yes = parseResult.GetValue(yesOption);

            var cancelled = false;

            var exitCode = ErrorReporting.Run(() =>
            {
                var profile = profileResolver.Resolve(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);
                var pruneService = new PruneService(services.Repository, services.ContentStore, services.RunLock);

                void RunPrune()
                {
                    var result = pruneService.Prune(profile);
                    PruneOutcomeReporter.Report(AnsiConsole.Console, result);
                }

                var eligibleCount = pruneService.CountEligibleForRemoval(profile);

                if (eligibleCount == 0 || yes)
                {
                    RunPrune();
                    return;
                }

                if (Console.IsInputRedirected)
                {
                    // Non-interactive session, no --yes: refuse rather than silently
                    // deleting data or silently doing nothing. Reported as a normal
                    // error (exit 1) by ErrorReporting, directing the user to --yes.
                    throw new PruneConfirmationRequiredException(profile.Name, eligibleCount);
                }

                // Interactive session, no --yes: ask before pruning. The prompt goes to
                // stderr so it's still visible even if stdout is redirected.
                var confirmed = StandardError.Console.Confirm(
                    $"This will permanently remove {eligibleCount} snapshot(s) and any content no longer referenced. Continue?",
                    defaultValue: false);

                if (confirmed)
                {
                    RunPrune();
                }
                else
                {
                    cancelled = true;
                }
            });

            if (cancelled)
            {
                OutcomeStyle.WriteLineNeutral(AnsiConsole.Console, "Prune cancelled: no snapshots removed.");
                return 0;
            }

            return exitCode;
        });

        return command;
    }
}
