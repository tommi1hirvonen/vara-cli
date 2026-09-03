using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using Vara.Application.Backup;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Application.Reporting;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
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
                OutcomeStyle.WriteLineError(StandardError.Console, "Error: specify either --at <date> or --version <id>.");
                return 1;
            }

            var cancelled = false;

            var exitCode = ErrorReporting.Run(() =>
            {
                var profile = profileResolver.Resolve(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);
                var console = AnsiConsole.Console;

                void RunRestore(bool overwrite)
                {
                    if (OutputMode.IsLiveCapable(console))
                    {
                        RunWithLiveDisplay(history, path, version, at, outPath, overwrite, console);
                    }
                    else
                    {
                        RunWithPlainOutput(history, path, version, at, outPath, overwrite, console);
                    }
                }

                try
                {
                    RunRestore(force);
                    OutcomeStyle.WriteLineSuccess(AnsiConsole.Console, $"Restored '{path}' to '{outPath}'.");
                }
                catch (DestinationExistsException) when (!force && !Console.IsInputRedirected)
                {
                    // Interactive session, no --force: ask before overwriting. The prompt goes to
                    // stderr so it's still visible even if stdout is redirected. Non-interactive
                    // sessions (stdin redirected) fall through and let the exception propagate to
                    // ErrorReporting, which reports it as a normal error (exit 1) directing the
                    // user to --force.
                    var overwrite = StandardError.Console.Confirm(
                        $"File '{Markup.Escape(outPath)}' already exists. Overwrite?", defaultValue: false);
                    if (overwrite)
                    {
                        RunRestore(overwrite: true);
                        OutcomeStyle.WriteLineSuccess(AnsiConsole.Console, $"Restored '{path}' to '{outPath}'.");
                    }
                    else
                    {
                        cancelled = true;
                    }
                }
            });

            if (cancelled)
            {
                OutcomeStyle.WriteLineNeutral(AnsiConsole.Console, "Restore cancelled: destination not overwritten.");
                return 0;
            }

            return exitCode;
        });

        return command;
    }

    // Interactive path: a single task is created as soon as the version's total size is
    // known (via RestoreProgressReporter.OnSizeResolved, invoked by SnapshotHistoryService
    // right before extraction begins) so the bar starts immediately at 0% against a known
    // total - no separate scan/indeterminate phase, per design.md's "no scan/indeterminate
    // phase is needed" decision. RemainingTimeColumn/TransferSpeedColumn compute ETA/throughput
    // natively from the task's own Value/MaxValue progression, so no BackupProgressCalculator-
    // style hand-rolled math is needed for this path.
    private static void RunWithLiveDisplay(
        SnapshotHistoryService history,
        string path,
        long? version,
        string? at,
        string outPath,
        bool overwrite,
        IAnsiConsole console)
    {
        var displayGate = new ProgressDisplayGate();

        console.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn(), new TransferSpeedColumn())
            .Start(ctx =>
            {
                ProgressTask? task = null;

                void Render(BackupProgress admitted)
                {
                    task ??= ctx.AddTask($"Restoring '{Markup.Escape(path)}'", autoStart: true, maxValue: admitted.TotalBytes);
                    task.Value = admitted.BytesTransferred;
                    ctx.Refresh();
                }

                var reporter = new RestoreProgressReporter(displayGate, Render);
                RunRestoreOnce(history, path, version, at, outPath, overwrite, reporter.OnBytesCopied, reporter.OnSizeResolved);
            });
    }

    // Non-interactive/redirected path: plain, uncolored, appended lines with no in-place
    // redraw, per the cli-presentation delta's "Non-interactive or color-incapable output
    // falls back to plain text" requirement - analogous to BackupCommand.RunWithPlainOutput.
    private static void RunWithPlainOutput(
        SnapshotHistoryService history,
        string path,
        long? version,
        string? at,
        string outPath,
        bool overwrite,
        IAnsiConsole console)
    {
        var calculator = new BackupProgressCalculator();
        var displayGate = new ProgressDisplayGate();

        void Render(BackupProgress admitted)
        {
            var snapshot = calculator.Calculate(admitted);
            var eta = snapshot.EstimatedTimeRemaining is { } remaining
                ? BackupRunSummaryFormatter.FormatDuration(remaining)
                : "calculating...";
            console.WriteLine(
                $"{BackupRunSummaryFormatter.FormatBytes(snapshot.BytesTransferred)} / {BackupRunSummaryFormatter.FormatBytes(snapshot.TotalBytes)} " +
                $"({snapshot.PercentComplete:0.0}%) - {BackupRunSummaryFormatter.FormatBytes((long)snapshot.ThroughputBytesPerSecond)}/s - ETA {eta}");
        }

        var reporter = new RestoreProgressReporter(displayGate, Render);
        RunRestoreOnce(history, path, version, at, outPath, overwrite, reporter.OnBytesCopied, reporter.OnSizeResolved);
    }

    private static void RunRestoreOnce(
        SnapshotHistoryService history,
        string path,
        long? version,
        string? at,
        string outPath,
        bool overwrite,
        Action<long> onBytesCopied,
        Action<long> onSizeResolved)
    {
        if (version is not null)
        {
            history.RestoreVersion(path, version.Value, outPath, overwrite, onBytesCopied, onSizeResolved);
        }
        else
        {
            var asOf = DateTimeOffset.Parse(at!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
            history.RestoreAsOf(path, asOf, outPath, overwrite, onBytesCopied, onSizeResolved);
        }
    }
}

/// <summary>
/// Wires a restore's raw <c>onBytesCopied</c> chunk callback and the <c>onSizeResolved</c>
/// callback (which <see cref="SnapshotHistoryService"/> invokes once, right before extraction
/// begins) into a single rate-limited progress render, reusing <see cref="Backup.BackupProgress"/>
/// + <see cref="ProgressDisplayGate"/> exactly as <see cref="BackupCommand"/> does, per
/// design.md's "Rate-limiting reuses ProgressDisplayGate" decision. Stateful per restore
/// attempt - construct a fresh instance per <c>RestoreVersion</c>/<c>RestoreAsOf</c> call.
/// </summary>
internal sealed class RestoreProgressReporter(ProgressDisplayGate gate, Action<BackupProgress> render)
{
    private long _totalBytes;
    private long _bytesCopiedSoFar;

    /// <summary>
    /// Reports the resolved version's total size immediately - bypassing the rate limit,
    /// since a bytes-copied value of 0 is always admitted as the run's first report - so a
    /// live progress bar can size itself and render at 0% right away instead of waiting for
    /// the first copied chunk.
    /// </summary>
    public void OnSizeResolved(long size)
    {
        _totalBytes = size;
        gate.Report(new BackupProgress(0, size), render);
    }

    public void OnBytesCopied(long chunk)
    {
        _bytesCopiedSoFar += chunk;
        gate.Report(new BackupProgress(_bytesCopiedSoFar, _totalBytes), render);
    }
}


