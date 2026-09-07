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
        var profileOption = new Option<string>("--profile") { Description = "The profile to prune.", Required = true };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var yesOption = new Option<bool>("--yes", "-y") { Description = "Confirm the run without prompting, even if snapshots are eligible for removal." };
        var command = new Command("prune", "Apply a profile's retention policy: remove expired snapshots and unreferenced content.") { profileOption, configOption, yesOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption)!;
            var configPath = parseResult.GetValue(configOption);
            var yes = parseResult.GetValue(yesOption);

            var cancelled = false;

            var exitCode = ErrorReporting.Run(() =>
            {
                var profile = profileResolver.Resolve(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);
                var pruneService = new PruneService(services.Repository, services.ContentStore, services.RunLock);

                void RunPrune(IReadOnlyList<long>? confirmedSnapshotIds)
                {
                    var console = AnsiConsole.Console;
                    var result = OutputMode.IsLiveCapable(console)
                        ? RunWithLiveDisplay(pruneService, profile, console, confirmedSnapshotIds)
                        : RunWithPlainOutput(pruneService, profile, console, confirmedSnapshotIds);
                    PruneOutcomeReporter.Report(console, result);
                }

                var eligibleIds = pruneService.ListEligibleForRemoval(profile);

                if (eligibleIds.Count == 0 || yes)
                {
                    // Nothing was shown to the user to confirm (either nothing is eligible, or
                    // --yes bypassed the prompt entirely), so Prune evaluates eligibility fresh.
                    RunPrune(confirmedSnapshotIds: null);
                    return;
                }

                if (Console.IsInputRedirected)
                {
                    // Non-interactive session, no --yes: refuse rather than silently
                    // deleting data or silently doing nothing. Reported as a normal
                    // error (exit 1) by ErrorReporting, directing the user to --yes.
                    throw new PruneConfirmationRequiredException(profile.Name, eligibleIds.Count);
                }

                // Interactive session, no --yes: ask before pruning. The prompt goes to
                // stderr so it's still visible even if stdout is redirected.
                var confirmed = StandardError.Console.Confirm(
                    $"This will permanently remove {eligibleIds.Count} snapshot(s) and any content no longer referenced. Continue?",
                    defaultValue: false);

                if (confirmed)
                {
                    // Pass exactly the ids shown at the prompt, so a snapshot that becomes
                    // eligible only after this point is never removed by this run.
                    RunPrune(confirmedSnapshotIds: eligibleIds);
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

    // Interactive path: an indeterminate spinner covers the DB/diff steps (listing
    // snapshots, evaluating retention, computing unreferenced blobs), whose duration
    // isn't practically predictable upfront; the first PruneProgress report - which
    // only ever arrives once the blob-deletion loop is about to start - swaps it for a
    // count-based bar, per design.md's "replace the indeterminate indicator on the
    // first admitted event" pattern (the same one BackupCommand uses for its own
    // scan-to-transfer transition). No ProgressDisplayGate is needed here: prune's
    // callback only updates an in-memory ProgressTask.Value on every iteration and
    // never forces its own redraw, so Spectre's own AutoRefresh already bounds the
    // display's redraw rate.
    private static PruneResult RunWithLiveDisplay(PruneService pruneService, Vara.Core.Configuration.Profile profile, IAnsiConsole console, IReadOnlyList<long>? confirmedSnapshotIds)
    {
        PruneResult result = null!;
        console.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn())
            .Start(ctx =>
            {
                var task = ctx.AddTask("Evaluating...", autoStart: true);
                task.IsIndeterminate = true;

                void Render(PruneProgress p)
                {
                    if (task.IsIndeterminate)
                    {
                        task.IsIndeterminate = false;
                        task.MaxValue = p.TotalBlobs;
                    }

                    task.Value = p.BlobsDeleted;
                    task.Description = $"Deleting blobs {p.BlobsDeleted} / {p.TotalBlobs}";
                }

                var progress = new Progress<PruneProgress>(Render);
                result = pruneService.Prune(profile, progress, confirmedSnapshotIds);

                // Ensures the final count is shown immediately rather than waiting for
                // the next AutoRefresh timer tick, per design.md's "single explicit
                // Refresh() after the deletion loop completes" decision.
                ctx.Refresh();
            });

        return result;
    }

    // Non-interactive/redirected path: plain, appended lines with no in-place redraw,
    // per the cli-presentation delta's "Non-interactive or color-incapable output
    // falls back to plain text" requirement.
    private static PruneResult RunWithPlainOutput(PruneService pruneService, Vara.Core.Configuration.Profile profile, IAnsiConsole console, IReadOnlyList<long>? confirmedSnapshotIds)
    {
        console.WriteLine("Evaluating...");

        var progress = new Progress<PruneProgress>(p =>
            console.WriteLine($"Removed {p.BlobsDeleted} of {p.TotalBlobs} blobs."));

        return pruneService.Prune(profile, progress, confirmedSnapshotIds);
    }
}
