using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using Vara.Application.Backup;
using Vara.Application.Profiles;
using Vara.Application.Reporting;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.Abstractions;

namespace Vara.Cli.Commands;

public static class BackupCommand
{
    // Bounded per design.md's "bounded timeout ... best-effort" decision: long enough
    // to cover the handful of reports racing the final one in practice, short enough
    // that a stuck callback can never hang the CLI's exit.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    public static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        IFileSystemScanner scanner,
        IHasher hasher,
        TextWriter? jsonOutput = null,
        IAnsiConsole? console = null,
        CancellationToken cancellationToken = default) =>
        Create(
            profileResolver,
            serviceFactory,
            scanner,
            hasher,
            jsonOutput,
            console,
            powerRequestFactory: null,
            errorConsole: null,
            cancellationToken);

    internal static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        IFileSystemScanner scanner,
        IHasher hasher,
        TextWriter? jsonOutput,
        IAnsiConsole? console,
        IBackupPowerRequestFactory? powerRequestFactory,
        IAnsiConsole? errorConsole,
        CancellationToken cancellationToken)
    {
        // Both default to the real ambient console/standard output - overridable so
        // tests can assert against in-memory equivalents instead of the process-wide
        // AnsiConsole.Console/Console.Out statics (which would otherwise race other
        // tests' console output under the test suite's default parallelization), the
        // same testability seam BackupPipeline's own diagnostics writer and
        // ErrorReporting.Run's errorConsole parameter use.
        var jsonWriter = jsonOutput ?? Console.Out;
        var ansiConsole = console ?? AnsiConsole.Console;

        var profileArgument = new Argument<string>("profile")
        {
            Description = "The profile to back up."
        };
        var configOption = CommandOptions.Config();
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview the planned added/changed/moved/deleted changes " +
                "and bytes to transfer without performing any writes."
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Print a single machine-readable JSON summary of the outcome to stdout " +
                "instead of the human-oriented output, and suppress the progress display."
            };
        var command = new Command("backup", "Run an incremental backup for a profile.")
        {
            profileArgument, configOption, dryRunOption, jsonOption
        };

        command.SetAction(async (parseResult, _) =>
        {
            var profileName = parseResult.GetValue(profileArgument)!;
            var configPath = parseResult.GetValue(configOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var json = parseResult.GetValue(jsonOption);

            // Populated by whichever branch below runs, regardless of mode - so the
            // exit-code check after ErrorReporting.RunAsync stays a single, uniform check
            // instead of four mode-specific ones (cli-presentation's "Outcome severity
            // determines process exit code" requirement).
            IReadOnlyList<string> failedPaths = [];
            var cancelled = false;

            var exitCode = await ErrorReporting.RunAsync(async () =>
            {
                var profile = profileResolver.Resolve(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);

                var pipeline = new BackupPipeline(scanner, hasher, services.ContentStore, services.Repository, services.RunLock);

                if (dryRun)
                {
                    var startedAt = DateTimeOffset.UtcNow;
                    var summary = !json && OutputMode.IsLiveCapable(ansiConsole)
                        ? RunDryRunWithLiveDisplay(() => pipeline.PlanOnly(profile), ansiConsole)
                        : pipeline.PlanOnly(profile);
                    var completedAt = DateTimeOffset.UtcNow;
                    failedPaths = summary.FailedPaths;

                    if (json)
                    {
                        WriteJson(jsonWriter, summary.ToJson(profile.Name, startedAt, completedAt));
                    }
                    else
                    {
                        BackupPlanOutcomeReporter.Report(ansiConsole, summary);
                    }

                    return;
                }

                await RunWithPowerRequestAsync(
                    powerRequestFactory ?? WindowsBackupPowerRequestFactory.Instance,
                    errorConsole ?? StandardError.Console,
                    async () =>
                    {
                        if (json)
                        {
                            // No IProgress<T> callback: --json suppresses progress display
                            // entirely, per the cli-presentation delta's "Machine-readable JSON
                            // output for the backup command" requirement.
                            var jsonResult = await pipeline.RunAsync(profile, progress: null, cancellationToken: cancellationToken);
                            failedPaths = jsonResult.FailedPaths;
                            cancelled = jsonResult.Cancelled;
                            WriteJson(jsonWriter, jsonResult.ToJson(profile.Name));
                            return;
                        }

                        var result = OutputMode.IsLiveCapable(ansiConsole)
                            ? await RunWithLiveDisplayAsync(pipeline, profile, ansiConsole, cancellationToken)
                            : await RunWithPlainOutputAsync(pipeline, profile, ansiConsole, cancellationToken);
                        failedPaths = result.FailedPaths;
                        cancelled = result.Cancelled;

                        ansiConsole.WriteLine();
                        BackupOutcomeReporter.Report(ansiConsole, result);
                    });
            });

            // A hard error (ExitCodes.HardError) always wins: it means the run never
            // finished, so it must not be downgraded to the partial-failure code. Only a
            // run (or dry run) that completed (ExitCodes.Success) but recorded failed
            // paths - or was gracefully cancelled via Ctrl+C - is promoted, per the
            // cli-presentation delta's "Outcome severity determines process exit code"
            // requirement.
            if (exitCode == ExitCodes.Success && (failedPaths.Count > 0 || cancelled))
            {
                exitCode = ExitCodes.PartialFailure;
            }

            return exitCode;
        });

        return command;
    }

    private static async Task RunWithPowerRequestAsync(
        IBackupPowerRequestFactory powerRequestFactory,
        IAnsiConsole errorConsole,
        Func<Task> run)
    {
        IDisposable? request;
        try
        {
            request = powerRequestFactory.Acquire();
        }
        catch (PowerRequestException exception)
        {
            ReportPowerRequestFailure(errorConsole, "could not be activated", exception);
            await run();
            return;
        }

        try
        {
            await run();
        }
        finally
        {
            if (request is not null)
            {
                try
                {
                    request.Dispose();
                }
                catch (PowerRequestException exception)
                {
                    ReportPowerRequestFailure(errorConsole, "could not be released", exception);
                }
            }
        }
    }

    private static void ReportPowerRequestFailure(IAnsiConsole errorConsole, string operation, Exception exception) =>
        OutcomeStyle.WriteLinePartialFailure(
            errorConsole,
            $"Warning: Windows sleep prevention {operation}: {exception.Message}");

    // Bypasses AnsiConsole entirely so --json's payload can never be word-wrapped to
    // the terminal width, styled, or otherwise altered by Spectre - stdout must carry
    // exactly one parseable JSON object and nothing else, per the cli-presentation
    // delta's requirement. Writes to the given writer (the real Console.Out by
    // default) rather than always reading Console.Out directly, so a test can assert
    // against an in-memory writer instead - see this method's caller in Create.
    private static void WriteJson(TextWriter writer, BackupRunSummaryJson dto)
    {
        var json = JsonSerializer.Serialize(dto, BackupRunSummaryJsonContext.Default.BackupRunSummaryJson);
        writer.WriteLine(json);
    }

    // Interactive path: a native Progress() run hosting three synthetic tasks (scan
    // spinner, then bar, then stats), all rendered by one BackupProgressColumn, per
    // design.md's "one unified column" decision. Progress()'s own AutoRefresh (unlike
    // Live, which has none) is what keeps throughput/ETA advancing during a stall - no
    // manual heartbeat timer is needed any more.
    private static Task<BackupRunResult> RunWithLiveDisplayAsync(
        BackupPipeline pipeline,
        Vara.Core.Configuration.Profile profile,
        IAnsiConsole console,
        CancellationToken cancellationToken)
    {
        var calculator = new BackupProgressCalculator();
        var displayGate = new ProgressDisplayGate(renderFileCountAdvancesImmediately: false);
        var column = new BackupProgressColumn(calculator);

        return LiveProgress.RunAsync(
            console,
            [column],
            (ctx, terminalProgress) =>
            {
                var scanTask = ctx.AddTask("Scanning", autoStart: true);
                scanTask.IsIndeterminate = true;
                BackupProgressColumn.SetRole(scanTask, BackupProgressRole.Scan);

                ProgressTask? barTask = null;
                ProgressTask? statsTask = null;
                IReadOnlyList<string> activePaths = Array.Empty<string>();
                BackupProgressState? displayedState = null;

                void SetDisplayedState(BackupProgressState state)
                {
                    displayedState = state;
                    barTask!.State.Update(BackupProgressColumn.ProgressKey, (BackupProgressState _) => state);
                    statsTask!.Value = state.BytesTransferred;
                    statsTask.State.Update(BackupProgressColumn.ProgressKey, (BackupProgressState _) => state);
                }

                // Render and RefreshActivePaths are only ever invoked by the rendering run's
                // single consumer (and never after it returns), so they need no locking.
                void Render(BackupProgress admitted)
                {
                    if (barTask is null)
                    {
                        // First admitted progress event: the scan/plan phase has
                        // finished and transfer is about to begin - replace the scan
                        // spinner with the bar/stats rows, per the progress-reporting
                        // delta's "Indicator replaced once transfer begins" scenario.
                        ctx.RemoveTask(scanTask);
                        barTask = ctx.AddTask("Transfer", autoStart: true, maxValue: 100);
                        BackupProgressColumn.SetRole(barTask, BackupProgressRole.Bar);
                        statsTask = ctx.AddTask("Stats", autoStart: true, maxValue: admitted.TotalBytes);
                        BackupProgressColumn.SetRole(statsTask, BackupProgressRole.Stats);
                    }

                    var snapshot = calculator.Calculate(admitted);
                    terminalProgress?.SetProgress(snapshot.PercentComplete);
                    SetDisplayedState(new BackupProgressState(
                        admitted.BytesTransferred,
                        admitted.TotalBytes,
                        admitted.FilesTransferred,
                        admitted.TotalFiles,
                        activePaths,
                        snapshot));
                    ctx.Refresh();
                }

                void RefreshActivePaths(IReadOnlyList<string> refreshedPaths)
                {
                    activePaths = refreshedPaths.ToArray();
                    if (displayedState is not { } currentState)
                    {
                        return;
                    }

                    SetDisplayedState(currentState with { ActivePaths = activePaths });
                    ctx.Refresh();
                }

                void StampOutcome(BackupProgressOutcome outcome)
                {
                    BackupProgressColumn.SetOutcome(barTask ?? scanTask, outcome);
                    ctx.Refresh();
                }

                // Reports and active-path changes are published by the backup recorder; the
                // rendering run funnels them to one consumer and awaits it (bounded by
                // DrainTimeout) before the outcome is stamped, so no render can fire against
                // an already torn-down context.
                //
                // The bar's final color reflects the run's actual outcome, not the
                // byte-based percentage BackupProgressColumn happened to reach - stamped
                // once, after the run either returns or throws, per design.md's
                // "BackupCommand.RunWithLiveDisplay resolves and stamps the outcome
                // itself" decision. barTask is guaranteed non-null on a normal return
                // (onTransferPhaseStarting always fires before the run returns, even for a
                // zero-byte run); scanTask is still the active task if an exception is
                // thrown before transfer begins (e.g. during scanning/diffing/planning).
                return BackupProgressRendering.RunAsync(
                    displayGate,
                    Render,
                    RefreshActivePaths,
                    DrainTimeout,
                    (progress, onActivePathsChanged) =>
                        pipeline.RunAsync(profile, progress, onActivePathsChanged, cancellationToken),
                    ResolveOutcome,
                    StampOutcome);
            },
            startIndeterminate: true);
    }

    private static BackupPlanSummary RunDryRunWithLiveDisplay(Func<BackupPlanSummary> plan, IAnsiConsole console)
    {
        var column = new BackupProgressColumn(new BackupProgressCalculator());

        return LiveProgress.Run(
            console,
            [column],
            (ctx, _) =>
            {
                var scanTask = ctx.AddTask("Scanning", autoStart: true);
                scanTask.IsIndeterminate = true;
                BackupProgressColumn.SetRole(scanTask, BackupProgressRole.Scan);
                ctx.Refresh();

                try
                {
                    var summary = plan();
                    BackupProgressColumn.SetOutcome(scanTask, BackupProgressOutcome.Success);
                    ctx.Refresh();
                    return summary;
                }
                catch
                {
                    BackupProgressColumn.SetOutcome(scanTask, BackupProgressOutcome.Error);
                    ctx.Refresh();
                    throw;
                }
            },
            startIndeterminate: true);
    }

    /// <summary>
    /// Maps a completed run's result to the outcome the live progress bar's final
    /// color should reflect - a clean success (bar turns green) or a success with
    /// partial failures, including a gracefully cancelled run (bar turns/stays amber),
    /// per the progress-reporting delta's "Progress bar reflects run outcome severity"
    /// requirement. A hard error (the run throwing before completing) is handled
    /// separately, by <see cref="BackupProgressRendering"/>, since no
    /// <see cref="BackupRunResult"/> exists in that case.
    /// Internal and pure so it can be unit tested directly, without needing a real
    /// <see cref="BackupPipeline"/>/<see cref="Progress"/> run.
    /// </summary>
    internal static BackupProgressOutcome ResolveOutcome(BackupRunResult result) =>
        result.FailedPaths.Count == 0 && !result.Cancelled ? BackupProgressOutcome.Success : BackupProgressOutcome.PartialFailure;

    // Non-interactive/redirected path: plain, uncolored, appended lines with no
    // in-place redraw, per the cli-presentation delta's "Non-interactive or
    // color-incapable output falls back to plain text" requirement.
    private static Task<BackupRunResult> RunWithPlainOutputAsync(
        BackupPipeline pipeline,
        Vara.Core.Configuration.Profile profile,
        IAnsiConsole console,
        CancellationToken cancellationToken)
    {
        var calculator = new BackupProgressCalculator();
        var displayGate = new ProgressDisplayGate(renderFileCountAdvancesImmediately: true);

        void Render(BackupProgress admitted)
        {
            var snapshot = calculator.Calculate(admitted);
            var eta = BackupRunSummaryFormatter.FormatEta(snapshot.EstimatedTimeRemaining);
            console.WriteLine(
                $"{BackupRunSummaryFormatter.FormatBytes(snapshot.BytesTransferred)} / " +
                $"{BackupRunSummaryFormatter.FormatBytes(snapshot.TotalBytes)} " +
                $"({snapshot.PercentComplete:0.0}%) - Files {admitted.FilesTransferred} / {admitted.TotalFiles} - " +
                $"{BackupRunSummaryFormatter.FormatBytes((long)snapshot.ThroughputBytesPerSecond)}/s - ETA {eta}");
        }

        // The rendering run is awaited before returning, so no progress line can follow the
        // run summary.
        return BackupProgressRendering.RunAsync(
            displayGate,
            Render,
            refreshActivePaths: null,
            DrainTimeout,
            (progress, _) => pipeline.RunAsync(profile, progress, cancellationToken: cancellationToken),
            ResolveOutcome);
    }
}
