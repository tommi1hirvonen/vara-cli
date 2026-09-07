using System.CommandLine;
using System.Threading;
using Spectre.Console;
using Vara.Application.Integrity;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.Abstractions;

namespace Vara.Cli.Commands;

public static class CheckCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory, IHasher hasher, CancellationToken cancellationToken = default)
    {
        var profileOption = new Option<string>("--profile") { Description = "The profile to check.", Required = true };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var quickOption = new Option<bool>("--quick") { Description = "Only check that each referenced blob is physically present in the store, without re-reading and re-hashing its content." };
        var command = new Command("check", "Verify that content physically stored in a profile's target still matches the manifest.") { profileOption, configOption, quickOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption)!;
            var configPath = parseResult.GetValue(configOption);
            var quick = parseResult.GetValue(quickOption);

            IntegrityCheckResult result = null!;

            var exitCode = ErrorReporting.Run(() =>
            {
                var profile = profileResolver.Resolve(profileName, configPath);

                // Read-only command: never create a profile's backing storage as a
                // side effect of merely checking it (see ProfileServiceFactory's
                // createIfMissing doc), consistent with the other read-only commands.
                using var services = serviceFactory.CreateFor(profile, createIfMissing: false);
                var checkService = new IntegrityCheckService(services.Repository, services.ContentStore, hasher);

                var console = AnsiConsole.Console;
                result = OutputMode.IsLiveCapable(console)
                    ? RunWithLiveDisplay(checkService, quick, console, cancellationToken)
                    : RunWithPlainOutput(checkService, quick, console, cancellationToken);

                CheckOutcomeReporter.Report(console, result);
            });

            // A hard error always wins (nothing was checked). Otherwise, per the
            // backup-integrity spec's "Check exits with a scriptable status"
            // requirement, any missing or corrupt blob promotes an otherwise-successful
            // run to the partial-failure code - an orphaned blob never does, since it is
            // informational only. A run gracefully cancelled via Ctrl+C is promoted the
            // same way, mirroring backup's own graceful-cancellation exit code, even when
            // no problems were found before the stop.
            if (exitCode == ExitCodes.Success && (result.Missing.Count > 0 || result.Corrupt.Count > 0 || result.Cancelled))
            {
                exitCode = ExitCodes.PartialFailure;
            }

            return exitCode;
        });

        return command;
    }

    // Interactive path: an indeterminate spinner covers the initial referenced/stored
    // hash enumeration, whose duration isn't practically predictable upfront; the
    // first IntegrityCheckProgress report - which only ever arrives once the
    // per-blob verification loop is about to start - swaps it for a count-based bar,
    // mirroring PruneCommand's own indeterminate-to-determinate swap.
    private static IntegrityCheckResult RunWithLiveDisplay(IntegrityCheckService checkService, bool quick, IAnsiConsole console, CancellationToken cancellationToken)
    {
        IntegrityCheckResult result = null!;
        console.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn())
            .Start(ctx =>
            {
                var task = ctx.AddTask("Enumerating...", autoStart: true);
                task.IsIndeterminate = true;

                void Render(IntegrityCheckProgress p)
                {
                    if (task.IsIndeterminate)
                    {
                        task.IsIndeterminate = false;
                        task.MaxValue = p.TotalBlobs;
                    }

                    task.Value = p.BlobsChecked;
                    task.Description = $"Checking blobs {p.BlobsChecked} / {p.TotalBlobs}";
                }

                var progress = new Progress<IntegrityCheckProgress>(Render);
                result = checkService.Check(quick, progress, cancellationToken);

                // Ensures the final count is shown immediately rather than waiting for
                // the next AutoRefresh timer tick, mirroring PruneCommand's own single
                // explicit Refresh() after its loop completes.
                ctx.Refresh();
            });

        return result;
    }

    // Non-interactive/redirected path: plain, appended lines with no in-place redraw,
    // per the cli-presentation delta's "Non-interactive or color-incapable output
    // falls back to plain text" requirement.
    private static IntegrityCheckResult RunWithPlainOutput(IntegrityCheckService checkService, bool quick, IAnsiConsole console, CancellationToken cancellationToken)
    {
        console.WriteLine("Enumerating...");

        var progress = new Progress<IntegrityCheckProgress>(p =>
            console.WriteLine($"Checked {p.BlobsChecked} of {p.TotalBlobs} blobs."));

        return checkService.Check(quick, progress, cancellationToken);
    }
}
