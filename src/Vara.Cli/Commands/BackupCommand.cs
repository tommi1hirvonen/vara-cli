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
    public static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        IFileSystemScanner scanner,
        IHasher hasher,
        CancellationToken cancellationToken = default,
        TextWriter? jsonOutput = null,
        IAnsiConsole? console = null) =>
        Create(
            profileResolver,
            serviceFactory,
            scanner,
            hasher,
            cancellationToken,
            jsonOutput,
            console,
            powerRequestFactory: null,
            errorConsole: null);

    internal static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        IFileSystemScanner scanner,
        IHasher hasher,
        CancellationToken cancellationToken,
        TextWriter? jsonOutput,
        IAnsiConsole? console,
        IBackupPowerRequestFactory? powerRequestFactory,
        IAnsiConsole? errorConsole)
    {
        // Both default to the real ambient console/standard output - overridable so
        // tests can assert against in-memory equivalents instead of the process-wide
        // AnsiConsole.Console/Console.Out statics (which would otherwise race other
        // tests' console output under the test suite's default parallelization), the
        // same testability seam BackupPipeline's own diagnostics writer and
        // ErrorReporting.Run's errorConsole parameter use.
        var jsonWriter = jsonOutput ?? Console.Out;
        var ansiConsole = console ?? AnsiConsole.Console;

        var profileArgument = new Argument<string>("profile") { Description = "The profile to back up." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Preview the planned added/changed/moved/deleted changes and bytes to transfer without performing any writes." };
        var jsonOption = new Option<bool>("--json") { Description = "Print a single machine-readable JSON summary of the outcome to stdout instead of the human-oriented output, and suppress the progress display." };
        var command = new Command("backup", "Run an incremental backup for a profile.") { profileArgument, configOption, dryRunOption, jsonOption };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileArgument)!;
            var configPath = parseResult.GetValue(configOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var json = parseResult.GetValue(jsonOption);

            // Populated by whichever branch below runs, regardless of mode - so the
            // exit-code check after ErrorReporting.Run stays a single, uniform check
            // instead of four mode-specific ones (cli-presentation's "Outcome severity
            // determines process exit code" requirement).
            IReadOnlyList<string> failedPaths = [];
            var cancelled = false;

            var exitCode = ErrorReporting.Run(() =>
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

                RunWithPowerRequest(
                    powerRequestFactory ?? WindowsBackupPowerRequestFactory.Instance,
                    errorConsole ?? StandardError.Console,
                    () =>
                    {
                        if (json)
                        {
                            // No IProgress<T> callback: --json suppresses progress display
                            // entirely, per the cli-presentation delta's "Machine-readable JSON
                            // output for the backup command" requirement.
                            var jsonResult = pipeline.Run(profile, progress: null, cancellationToken);
                            failedPaths = jsonResult.FailedPaths;
                            cancelled = jsonResult.Cancelled;
                            WriteJson(jsonWriter, jsonResult.ToJson(profile.Name));
                            return;
                        }

                        var result = OutputMode.IsLiveCapable(ansiConsole)
                            ? RunWithLiveDisplay(pipeline, profile, ansiConsole, cancellationToken)
                            : RunWithPlainOutput(pipeline, profile, ansiConsole, cancellationToken);
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

    private static void RunWithPowerRequest(
        IBackupPowerRequestFactory powerRequestFactory,
        IAnsiConsole errorConsole,
        Action run)
    {
        IDisposable? request;
        try
        {
            request = powerRequestFactory.Acquire();
        }
        catch (PowerRequestException exception)
        {
            ReportPowerRequestFailure(errorConsole, "could not be activated", exception);
            run();
            return;
        }

        try
        {
            run();
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
    private static BackupRunResult RunWithLiveDisplay(BackupPipeline pipeline, Vara.Core.Configuration.Profile profile, IAnsiConsole console, CancellationToken cancellationToken)
    {
        // Bounded per design.md's "bounded timeout ... best-effort" decision: long enough
        // to cover the handful of reports racing the final one in practice, short enough
        // that a stuck callback can never hang the CLI's exit.
        var drainTimeout = TimeSpan.FromSeconds(2);
        var calculator = new BackupProgressCalculator();
        var displayGate = new ProgressDisplayGate(renderFileCountAdvancesImmediately: false);
        var column = new BackupProgressColumn(calculator);

        BackupRunResult result = null!;
        console.Progress()
            .Columns(column)
            .Start(ctx =>
            {
                var scanTask = ctx.AddTask("Scanning", autoStart: true);
                scanTask.IsIndeterminate = true;
                scanTask.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => BackupProgressRole.Scan);

                ProgressTask? barTask = null;
                ProgressTask? statsTask = null;
                var displaySync = new object();
                var displayOpen = true;
                IReadOnlyList<string> activePaths = Array.Empty<string>();
                BackupProgressState? displayedState = null;

                void SetDisplayedState(BackupProgressState state)
                {
                    displayedState = state;
                    barTask!.State.Update(BackupProgressColumn.ProgressKey, (BackupProgressState _) => state);
                    statsTask!.Value = state.BytesTransferred;
                    statsTask.State.Update(BackupProgressColumn.ProgressKey, (BackupProgressState _) => state);
                }

                void Render(BackupProgress admitted)
                {
                    lock (displaySync)
                    {
                        if (!displayOpen)
                        {
                            return;
                        }

                        if (barTask is null)
                        {
                            // First admitted progress event: the scan/plan phase has
                            // finished and transfer is about to begin - replace the scan
                            // spinner with the bar/stats rows, per the progress-reporting
                            // delta's "Indicator replaced once transfer begins" scenario.
                            ctx.RemoveTask(scanTask);
                            barTask = ctx.AddTask("Transfer", autoStart: true, maxValue: 100);
                            barTask.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => BackupProgressRole.Bar);
                            statsTask = ctx.AddTask("Stats", autoStart: true, maxValue: admitted.TotalBytes);
                            statsTask.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => BackupProgressRole.Stats);
                        }

                        var snapshot = calculator.Calculate(admitted);
                        SetDisplayedState(new BackupProgressState(
                            admitted.BytesTransferred,
                            admitted.TotalBytes,
                            admitted.FilesTransferred,
                            admitted.TotalFiles,
                            activePaths,
                            snapshot));
                        ctx.Refresh();
                    }
                }

                void RefreshActivePaths(IReadOnlyList<string> refreshedPaths)
                {
                    lock (displaySync)
                    {
                        if (!displayOpen)
                        {
                            return;
                        }

                        activePaths = refreshedPaths.ToArray();
                        if (displayedState is not { } currentState)
                        {
                            return;
                        }

                        SetDisplayedState(currentState with { ActivePaths = activePaths });
                        ctx.Refresh();
                    }
                }

                // Progress<T> has no SynchronizationContext to capture in a console app,
                // so it always dispatches Report callbacks via the thread pool,
                // asynchronously relative to the call site - including the run's final
                // report, which can race pipeline.Run's return. TrackedProgress mimics
                // that same thread-pool dispatch but also tracks outstanding deliveries,
                // so drainTimeout below can bound how long teardown waits for them,
                // per design.md's "drain" decision.
                var progress = new TrackedProgress<BackupProgress>(p => displayGate.Report(p, Render));
                var activePathRefresh = new CoalescedActivePathRefreshDispatcher(RefreshActivePaths);

                void DrainCallbacks()
                {
                    var deadline = DateTime.UtcNow + drainTimeout;
                    progress.TryDrain(drainTimeout);
                    var remaining = deadline - DateTime.UtcNow;
                    activePathRefresh.TryDrain(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                }

                void CloseDisplay(BackupProgressOutcome outcome)
                {
                    lock (displaySync)
                    {
                        if (!displayOpen)
                        {
                            return;
                        }

                        displayOpen = false;
                        (barTask ?? scanTask).State.Update(BackupProgressColumn.OutcomeKey, (BackupProgressOutcome _) => outcome);
                        ctx.Refresh();
                    }
                }

                // The bar's final color reflects the run's actual outcome, not the
                // byte-based percentage BackupProgressColumn happened to reach - stamped
                // once here, after the run either returns or throws, per design.md's
                // "BackupCommand.RunWithLiveDisplay resolves and stamps the outcome
                // itself" decision. barTask is guaranteed non-null on a normal return
                // (onTransferPhaseStarting always fires before Run returns, even for a
                // zero-byte run); scanTask is still the active task if an exception is
                // thrown before transfer begins (e.g. during scanning/diffing/planning).
                try
                {
                    result = pipeline.Run(profile, progress, cancellationToken, activePathRefresh.Request);
                    DrainCallbacks();
                    activePathRefresh.ThrowIfFaulted();
                    activePathRefresh.Close();

                    var outcome = ResolveOutcome(result);
                    CloseDisplay(outcome);
                }
                catch
                {
                    DrainCallbacks();
                    activePathRefresh.Close();
                    CloseDisplay(BackupProgressOutcome.Error);
                    throw;
                }
            });

        return result;
    }

    private static BackupPlanSummary RunDryRunWithLiveDisplay(Func<BackupPlanSummary> plan, IAnsiConsole console)
    {
        var column = new BackupProgressColumn(new BackupProgressCalculator());
        BackupPlanSummary summary = null!;

        console.Progress()
            .Columns(column)
            .Start(ctx =>
            {
                var scanTask = ctx.AddTask("Scanning", autoStart: true);
                scanTask.IsIndeterminate = true;
                scanTask.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => BackupProgressRole.Scan);
                ctx.Refresh();

                try
                {
                    summary = plan();
                    scanTask.State.Update(BackupProgressColumn.OutcomeKey, (BackupProgressOutcome _) => BackupProgressOutcome.Success);
                    ctx.Refresh();
                }
                catch
                {
                    scanTask.State.Update(BackupProgressColumn.OutcomeKey, (BackupProgressOutcome _) => BackupProgressOutcome.Error);
                    ctx.Refresh();
                    throw;
                }
            });

        return summary;
    }

    /// <summary>
    /// Maps a completed run's result to the outcome the live progress bar's final
    /// color should reflect - a clean success (bar turns green) or a success with
    /// partial failures, including a gracefully cancelled run (bar turns/stays amber),
    /// per the progress-reporting delta's "Progress bar reflects run outcome severity"
    /// requirement. A hard error (the run throwing before completing) is handled
    /// separately, in <see cref="RunWithLiveDisplay"/>'s catch block, since no
    /// <see cref="BackupRunResult"/> exists in that case.
    /// Internal and pure so it can be unit tested directly, without needing a real
    /// <see cref="BackupPipeline"/>/<see cref="Progress"/> run.
    /// </summary>
    internal static BackupProgressOutcome ResolveOutcome(BackupRunResult result) =>
        result.FailedPaths.Count == 0 && !result.Cancelled ? BackupProgressOutcome.Success : BackupProgressOutcome.PartialFailure;

    // Non-interactive/redirected path: plain, uncolored, appended lines with no
    // in-place redraw, per the cli-presentation delta's "Non-interactive or
    // color-incapable output falls back to plain text" requirement.
    private static BackupRunResult RunWithPlainOutput(BackupPipeline pipeline, Vara.Core.Configuration.Profile profile, IAnsiConsole console, CancellationToken cancellationToken)
    {
        var calculator = new BackupProgressCalculator();
        var displayGate = new ProgressDisplayGate(renderFileCountAdvancesImmediately: true);

        void Render(BackupProgress admitted)
        {
            var snapshot = calculator.Calculate(admitted);
            var eta = snapshot.EstimatedTimeRemaining is { } remaining
                ? BackupRunSummaryFormatter.FormatDuration(remaining)
                : "calculating...";
            console.WriteLine(
                $"{BackupRunSummaryFormatter.FormatBytes(snapshot.BytesTransferred)} / {BackupRunSummaryFormatter.FormatBytes(snapshot.TotalBytes)} " +
                $"({snapshot.PercentComplete:0.0}%) - Files {admitted.FilesTransferred} / {admitted.TotalFiles} - " +
                $"{BackupRunSummaryFormatter.FormatBytes((long)snapshot.ThroughputBytesPerSecond)}/s - ETA {eta}");
        }

        var progress = new Progress<BackupProgress>(p => displayGate.Report(p, Render));
        return pipeline.Run(profile, progress, cancellationToken);
    }
}
