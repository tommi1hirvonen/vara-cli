using System.CommandLine;
using Spectre.Console;
using Vara.Application.Integrity;
using Vara.Application.Profiles;
using Vara.Application.Reporting;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.Abstractions;

namespace Vara.Cli.Commands;

public static class CheckCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory, IHasher hasher, CancellationToken cancellationToken = default, IAnsiConsole? console = null)
    {
        var profileArgument = new Argument<string>("profile") { Description = "The profile to check." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var quickOption = new Option<bool>("--quick") { Description = "Only check that each referenced blob is physically present in the store, without re-reading and re-hashing its content." };
        var command = new Command("check", "Verify that content physically stored in a profile's target still matches the manifest.") { profileArgument, configOption, quickOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileArgument)!;
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

                var outputConsole = console ?? AnsiConsole.Console;
                result = OutputMode.IsLiveCapable(outputConsole)
                    ? RunWithLiveDisplay(checkService, quick, outputConsole, cancellationToken)
                    : RunWithPlainOutput(checkService, quick, outputConsole, cancellationToken);

                CheckOutcomeReporter.Report(outputConsole, result);
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
    // first IntegrityCheckProgress report swaps it for a percentage bar once total
    // workload is known, mirroring PruneCommand's indeterminate-to-determinate swap.
    private static IntegrityCheckResult RunWithLiveDisplay(IntegrityCheckService checkService, bool quick, IAnsiConsole console, CancellationToken cancellationToken)
    {
        IntegrityCheckResult result = null!;
        var calculator = new IntegrityCheckProgressCalculator();
        console.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
            .Start(ctx =>
            {
                var task = ctx.AddTask("Enumerating...", autoStart: true);
                task.IsIndeterminate = true;

                void Render(IntegrityCheckProgress p)
                {
                    if (task.IsIndeterminate)
                    {
                        task.IsIndeterminate = false;
                        task.MaxValue = 100;
                    }

                    var snapshot = calculator.Calculate(p, quick);
                    task.Value = snapshot.PercentComplete;
                    var eta = snapshot.EstimatedTimeRemaining is { } remaining
                        ? BackupRunSummaryFormatter.FormatDuration(remaining)
                        : "calculating...";
                    task.Description = $"Checking blobs {p.BlobsChecked} / {p.TotalBlobs} - ETA {eta}";
                }

                var progress = new InlineProgress<IntegrityCheckProgress>(Render);
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
        var calculator = new IntegrityCheckProgressCalculator();

        var progress = new InlineProgress<IntegrityCheckProgress>(p =>
        {
            var snapshot = calculator.Calculate(p, quick);
            var percent = snapshot.PercentComplete.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            var eta = snapshot.EstimatedTimeRemaining is { } remaining
                ? BackupRunSummaryFormatter.FormatDuration(remaining)
                : "calculating...";
            console.WriteLine($"Checked {p.BlobsChecked} of {p.TotalBlobs} blobs ({percent}%) - ETA {eta}.");
        });

        return checkService.Check(quick, progress, cancellationToken);
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
