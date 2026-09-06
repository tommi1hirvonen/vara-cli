using System.CommandLine;
using Spectre.Console;
using Vara.Application.Backup;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Application.Reporting;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.FileSystem;
using Vara.Core.Snapshots;

namespace Vara.Cli.Commands;

public static class RestoreCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory)
    {
        var profileOption = new Option<string?>("--profile") { Description = "The profile to restore from. Optional when the current directory is inside a profile's target root." };
        var pathArgument = new Argument<string>("path") { Description = "The file (or, with --recursive, directory) to restore - a mirror-relative path, an absolute source path, or a path relative to the current directory." };
        var outOption = new Option<string?>("--out") { Description = "Destination path to write the restored content to. Mutually exclusive with --in-place." };
        var inPlaceOption = new Option<bool>("--in-place") { Description = "Restore back to the original source location instead of an explicit --out destination. Mutually exclusive with --out." };
        var atOption = new Option<string?>("--at") { Description = "Restore the version current as of this date/time (for example, '2025-01-15' or '2025-01-15 14:30'). With --recursive, omitting this restores the directory's current tracked state." };
        var versionOption = new Option<long?>("--version") { Description = "Restore this specific version id (see the 'history' command). If neither this nor --at is given in an interactive session, a version picker is shown instead. Mutually exclusive with --recursive." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };
        var forceOption = new Option<bool>("--force") { Description = "Skip the overwrite confirmation (single-file restore) or the single directory-restore confirmation (--recursive) without prompting." };
        var recursiveOption = new Option<bool>("--recursive") { Description = "Restore an entire directory (subtree) instead of a single file, reconstructing its exact tracked state as of --at (or the current state if --at is omitted). Mutually exclusive with --version." };

        var command = new Command("restore", "Restore a file's historical content, without touching the live mirror.")
        {
            pathArgument, profileOption, outOption, inPlaceOption, atOption, versionOption, configOption, forceOption, recursiveOption,
        };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption);
            var path = parseResult.GetValue(pathArgument)!;
            var outPath = parseResult.GetValue(outOption);
            var inPlace = parseResult.GetValue(inPlaceOption);
            var at = parseResult.GetValue(atOption);
            var version = parseResult.GetValue(versionOption);
            var configPath = parseResult.GetValue(configOption);
            var force = parseResult.GetValue(forceOption);
            var recursive = parseResult.GetValue(recursiveOption);

            if (outPath is not null && inPlace)
            {
                OutcomeStyle.WriteLineError(StandardError.Console, "Error: --out and --in-place are mutually exclusive.");
                return 1;
            }

            if (outPath is null && !inPlace)
            {
                OutcomeStyle.WriteLineError(StandardError.Console, "Error: specify either --out <path> or --in-place.");
                return 1;
            }

            if (recursive && version is not null)
            {
                OutcomeStyle.WriteLineError(StandardError.Console, "Error: --recursive and --version are mutually exclusive.");
                return 1;
            }

            // Whether an interactive version picker can be shown when neither --at nor
            // --version is given - requires both a real, non-redirected input stream (to read
            // the user's selection) and a console capable of rendering the picker itself.
            // Only relevant to single-file restore; a recursive restore has no single per-file
            // version history to pick from (per the "Interactive version selection when
            // restoring" requirement's "does not apply to a recursive directory restore" note).
            var console = AnsiConsole.Console;
            var canPromptForVersion = !Console.IsInputRedirected && OutputMode.IsLiveCapable(console);

            if (!recursive && at is null && version is null && !canPromptForVersion)
            {
                OutcomeStyle.WriteLineError(StandardError.Console, "Error: specify either --at <date> or --version <id>.");
                return 1;
            }

            var cancelled = false;

            var exitCode = ErrorReporting.Run(() =>
            {
                var profile = profileResolver.ResolveForBrowsing(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

                if (recursive)
                {
                    RunRecursiveRestore(history, profile.TargetRoot, path, at, outPath, inPlace, force, console, ref cancelled);
                    return;
                }

                path = SnapshotPathResolver.TryResolve(
                    profile.TargetRoot,
                    path,
                    candidate => services.Repository.GetFileHistory(candidate).Count > 0,
                    out var resolvedPath)
                    ? resolvedPath
                    : path;

                if (at is null && version is null)
                {
                    // Neither given, but canPromptForVersion is true (checked above) -
                    // present the path's recorded versions as a selectable list per the
                    // snapshot-history delta's "Interactive version selection when
                    // restoring" requirement, reusing HistoryTablePresenter's per-row
                    // formatting for the prompt's choice labels.
                    var selectable = history.GetFileHistory(path)
                        .Where(v => v.ChangeKind != FileChangeKind.Deleted)
                        .ToList();

                    if (selectable.Count == 0)
                    {
                        throw new NoHistoryForPathException(path);
                    }

                    var selected = StandardError.Console.Prompt(
                        new SelectionPrompt<FileVersionRecord>()
                            .Title("Select a version to restore:")
                            .PageSize(10)
                            .UseConverter(v => $"#{v.Id} - {v.RecordedAt:yyyy-MM-dd HH:mm:ss zzz} - {v.ChangeKind} - {BackupRunSummaryFormatter.FormatBytes(v.Size)}")
                            .AddChoices(selectable));
                    version = selected.Id;
                }

                var effectiveOutPath = inPlace ? AbsolutePathMirrorMapper.FromMirrorPath(path) : outPath!;

                void RunRestore(bool overwrite)
                {
                    if (OutputMode.IsLiveCapable(console))
                    {
                        RunWithLiveDisplay(history, path, version, at, effectiveOutPath, overwrite, console);
                    }
                    else
                    {
                        RunWithPlainOutput(history, path, version, at, effectiveOutPath, overwrite, console);
                    }
                }

                try
                {
                    RunRestore(force);
                    OutcomeStyle.WriteLineSuccess(AnsiConsole.Console, $"Restored '{path}' to '{effectiveOutPath}'.");
                }
                catch (DestinationExistsException) when (!force && !Console.IsInputRedirected)
                {
                    // Interactive session, no --force: ask before overwriting. The prompt goes to
                    // stderr so it's still visible even if stdout is redirected. Non-interactive
                    // sessions (stdin redirected) fall through and let the exception propagate to
                    // ErrorReporting, which reports it as a normal error (exit 1) directing the
                    // user to --force.
                    var overwrite = StandardError.Console.Confirm(
                        $"File '{Markup.Escape(effectiveOutPath)}' already exists. Overwrite?", defaultValue: false);
                    if (overwrite)
                    {
                        RunRestore(overwrite: true);
                        OutcomeStyle.WriteLineSuccess(AnsiConsole.Console, $"Restored '{path}' to '{effectiveOutPath}'.");
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
            var asOf = DateTimeOptionParser.Parse("--at", at!);
            history.RestoreAsOf(path, asOf, outPath, overwrite, onBytesCopied, onSizeResolved);
        }
    }

    // --recursive path: plans the whole directory restore up front (PlanDirectoryRestore is
    // read-only), shows exactly one confirmation covering every planned write/removal (skipped
    // when --force is given), and only then executes - mirroring PruneCommand's
    // count-then-confirm-then-run shape for its own single confirmation, per design.md's
    // "single confirmation replaces per-file overwrite guarding" decision.
    private static void RunRecursiveRestore(
        SnapshotHistoryService history,
        string mirrorRoot,
        string path,
        string? at,
        string? outPath,
        bool inPlace,
        bool force,
        IAnsiConsole console,
        ref bool cancelled)
    {
        var resolvedPath = SnapshotPathResolver.TryResolve(
            mirrorRoot,
            path,
            candidate => IsDirectoryTracked(history, candidate),
            out var candidatePath)
            ? candidatePath
            : path;

        var asOf = at is null
            ? (DateTimeOffset?)null
            : DateTimeOptionParser.Parse("--at", at);

        var plan = history.PlanDirectoryRestore(resolvedPath, asOf, outPath, inPlace);

        void Execute()
        {
            if (OutputMode.IsLiveCapable(console))
            {
                RunDirectoryRestoreWithLiveDisplay(history, resolvedPath, plan, console);
            }
            else
            {
                RunDirectoryRestoreWithPlainOutput(history, plan, console);
            }

            OutcomeStyle.WriteLineSuccess(
                AnsiConsole.Console,
                $"Restored directory '{resolvedPath}': {plan.ToWrite.Count} file(s) written, {plan.ToRemove.Count} file(s) removed.");
        }

        if (force)
        {
            Execute();
            return;
        }

        if (Console.IsInputRedirected)
        {
            // Non-interactive session, no --force: refuse rather than silently writing and
            // removing potentially many files without explicit authorization. Reported as a
            // normal error (exit 1) by ErrorReporting, directing the user to --force.
            throw new RestoreDirectoryConfirmationRequiredException(resolvedPath, plan.ToWrite.Count, plan.ToRemove.Count);
        }

        // Interactive session, no --force: ask once before making any change, covering every
        // planned write and removal - replacing the per-file overwrite prompt single-file
        // restore uses, per the "Single confirmation for a directory restore" requirement. The
        // prompt goes to stderr so it's still visible even if stdout is redirected.
        var removalClause = plan.ToRemove.Count > 0 ? $" and remove {plan.ToRemove.Count} file(s)" : string.Empty;
        var confirmed = StandardError.Console.Confirm(
            $"This will write {plan.ToWrite.Count} file(s){removalClause} under '{Markup.Escape(resolvedPath)}'. Continue?",
            defaultValue: false);

        if (confirmed)
        {
            Execute();
        }
        else
        {
            cancelled = true;
        }
    }

    /// <summary>
    /// Whether any tracked path, at any point in history, falls under <paramref name="candidate"/> -
    /// used as <see cref="SnapshotPathResolver.TryResolve"/>'s "does this candidate match"
    /// predicate for a recursive restore's directory argument, reusing
    /// <see cref="SnapshotHistoryService.ListDirectory"/>'s own "any tracked path" check rather
    /// than duplicating it.
    /// </summary>
    private static bool IsDirectoryTracked(SnapshotHistoryService history, string candidate)
    {
        try
        {
            history.ListDirectory(candidate, asOf: null, includeDeleted: true);
            return true;
        }
        catch (NoSuchDirectoryException)
        {
            return false;
        }
    }

    // Interactive path for a recursive directory restore: one aggregate task spans the whole
    // operation - onSizeResolved (invoked once, with the plan's total byte count, before any
    // file is written) sizes it up front, exactly as the single-file path does, per design.md's
    // "Progress reporting reuses RestoreProgressReporter unmodified" decision.
    private static void RunDirectoryRestoreWithLiveDisplay(
        SnapshotHistoryService history,
        string directoryPath,
        DirectoryRestorePlan plan,
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
                    task ??= ctx.AddTask($"Restoring directory '{Markup.Escape(directoryPath)}'", autoStart: true, maxValue: admitted.TotalBytes);
                    task.Value = admitted.BytesTransferred;
                    ctx.Refresh();
                }

                var reporter = new RestoreProgressReporter(displayGate, Render);
                history.ExecuteDirectoryRestore(plan, reporter.OnBytesCopied, reporter.OnSizeResolved);
            });
    }

    // Non-interactive/redirected path for a recursive directory restore - analogous to
    // RunWithPlainOutput's single-file equivalent.
    private static void RunDirectoryRestoreWithPlainOutput(
        SnapshotHistoryService history,
        DirectoryRestorePlan plan,
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
        history.ExecuteDirectoryRestore(plan, reporter.OnBytesCopied, reporter.OnSizeResolved);
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


