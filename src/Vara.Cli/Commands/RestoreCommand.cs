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
    /// <param name="console">Where results and progress are written; defaults to the ambient console.</param>
    /// <param name="errorConsole">
    /// Where errors and interactive prompts are written; defaults to standard error, so they
    /// stay visible when standard output is redirected.
    /// </param>
    /// <param name="isInputRedirected">
    /// Whether standard input is redirected (so no prompt can be answered); defaults to
    /// <see cref="Console.IsInputRedirected"/>.
    /// </param>
    public static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        CancellationToken cancellationToken = default,
        IAnsiConsole? console = null,
        IAnsiConsole? errorConsole = null,
        Func<bool>? isInputRedirected = null)
    {
        // Overridable so tests can assert against an in-memory console instead of the
        // process-wide AnsiConsole.Console static (which would otherwise race other tests'
        // console output under the test suite's default parallelization) - the same
        // testability seam BackupCommand.Create's own console parameter uses.
        var ansiConsole = console ?? AnsiConsole.Console;
        var errorAnsiConsole = errorConsole ?? StandardError.Console;
        var inputRedirected = isInputRedirected ?? (() => Console.IsInputRedirected);

        var profileOption = CommandOptions.Profile("restore from");
        var pathArgument = new Argument<string>("path")
        {
            Description = "The file (or, with --recursive, directory) to restore - a mirror-relative path, " +
                "an absolute " +
                "source path, or a path relative to the current directory."
        };
        var outOption = new Option<string?>("--out")
        {
            Description = "Destination path to write the restored content to. Mutually exclusive with --in-place."
        };
        var inPlaceOption = new Option<bool>("--in-place")
        {
            Description = "Restore back to the original source location instead of an explicit --out destination. " +
                "Mutually exclusive with --out."
        };
        var atOption = new Option<string?>("--at")
        {
            Description = "Restore the version current as of this date/time (for example, '2025-01-15' or " +
                "'2025-01-15 14:30'). With --recursive, omitting this (and --snapshot) restores the directory's " +
                "current tracked state in a non-interactive session, or presents a snapshot picker interactively. " +
                "Mutually exclusive with --snapshot."
        };
        var versionOption = new Option<long?>("--version")
        {
            Description = "Restore this specific version id (see the 'history' command). If neither this nor --at " +
                "is given in an interactive session, a version picker is shown instead. Mutually exclusive with " +
                "--recursive."
        };
        var configOption = CommandOptions.Config();
        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip the overwrite confirmation (single-file restore) or the single directory-restore " +
                "confirmation (--recursive) without prompting."
        };
        var recursiveOption = new Option<bool>("--recursive")
        {
            Description = "Restore an entire directory (subtree) instead of a single file, reconstructing its exact " +
                "tracked state as of --at/--snapshot (or the current state if neither is given). " +
                "Mutually exclusive with --version."
        };
        var snapshotOption = new Option<long?>("--snapshot")
        {
            Description = "Restore the directory's tracked state as of this specific snapshot id " +
                "(see the 'snapshots' " +
                "command) - the non-interactive equivalent of selecting it from the --recursive snapshot picker. " +
                "Only valid with --recursive. Mutually exclusive with --at."
        };

        var command = new Command("restore", "Restore a file's historical content, without touching the live mirror.")
        {
            pathArgument, profileOption, outOption, inPlaceOption, atOption,
            versionOption, configOption, forceOption, recursiveOption, snapshotOption,
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
            var snapshotId = parseResult.GetValue(snapshotOption);

            if (CommandValidation.ExactlyOne(outPath is not null, inPlace, "--out <path>", "--in-place", errorAnsiConsole)
                    is { } outTargetInvalid)
            {
                return outTargetInvalid;
            }

            var optionConflict =
                CommandValidation.MutuallyExclusive(recursive, version is not null, "--recursive", "--version", errorAnsiConsole)
                ?? CommandValidation.MutuallyExclusive(at is not null, version is not null, "--at", "--version", errorAnsiConsole)
                ?? CommandValidation.MutuallyExclusive(snapshotId is not null, at is not null, "--snapshot", "--at", errorAnsiConsole);
            if (optionConflict is { } conflict)
            {
                return conflict;
            }

            if (snapshotId is not null && !recursive)
            {
                return ErrorReporting.Usage(
                    "--snapshot only applies to a recursive directory restore (--recursive).",
                    errorAnsiConsole);
            }

            // Whether an interactive picker can be shown when neither --at nor --version (for
            // a single-file restore) or neither --at nor --snapshot (for a --recursive
            // directory restore) is given - requires both a real, non-redirected input stream
            // (to read the user's selection) and a console capable of rendering the picker
            // itself. Single-file restore's picker offers the path's version history; a
            // --recursive restore's own picker (per the "Interactive and explicit snapshot
            // selection for a directory restore" requirement) offers candidate snapshots plus
            // a "current tracked state" entry instead, since a directory has no single
            // per-file version history to pick from.
            var console = ansiConsole;
            var canPromptForVersion = !inputRedirected() && OutputMode.IsLiveCapable(console);

            if (!recursive && at is null && version is null && !canPromptForVersion)
            {
                return ErrorReporting.Usage("specify either --at <date> or --version <id>.", errorAnsiConsole);
            }

            var request = new RestoreRequest(path, at, version, snapshotId, outPath, inPlace, force);
            var environment = new RestoreEnvironment(
                console, errorAnsiConsole, inputRedirected, canPromptForVersion, cancellationToken);
            var outcome = RestoreOutcome.Completed;

            var exitCode = ErrorReporting.Run(() =>
            {
                using var session = HistorySession.Open(profileResolver, serviceFactory, profileName, configPath);
                outcome = recursive
                    ? RunRecursiveRestore(session, request, environment)
                    : RunSingleFileRestore(session, request, environment);
            }, errorAnsiConsole);

            if (outcome == RestoreOutcome.Declined)
            {
                OutcomeStyle.WriteLineNeutral(console, "Restore cancelled: destination not overwritten.");
                return ExitCodes.Success;
            }

            // A gracefully cancelled recursive restore (via Ctrl+C, mid-run) reuses the same
            // partial-failure exit code as backup's own graceful cancellation - it did not
            // necessarily write/remove every planned path - distinct from success, and distinct
            // from a hard error, since whatever was already applied is not corrupted or lost.
            if (exitCode == ExitCodes.Success && outcome == RestoreOutcome.StoppedEarly)
            {
                exitCode = ExitCodes.PartialFailure;
            }

            return exitCode;
        });

        return command;
    }

    private enum RestoreOutcome
    {
        Completed,
        /// <summary>A graceful Ctrl+C stopped a recursive restore part-way.</summary>
        StoppedEarly,
        /// <summary>The user declined the overwrite / directory-restore confirmation.</summary>
        Declined,
    }

    private sealed record RestoreRequest(
        string Path,
        string? At,
        long? Version,
        long? SnapshotId,
        string? OutPath,
        bool InPlace,
        bool Force);

    private sealed record RestoreEnvironment(
        IAnsiConsole Console,
        IAnsiConsole ErrorConsole,
        Func<bool> IsInputRedirected,
        bool CanPromptForVersion,
        CancellationToken CancellationToken);

    private static RestoreOutcome RunSingleFileRestore(
        HistorySession session,
        RestoreRequest request,
        RestoreEnvironment environment)
    {
        var history = session.History;
        var fileVersions = session.FileVersions;
        var version = request.Version;
        var fileResolved = session.TryResolveFilePath(request.Path, out var resolvedPath);
        var path = fileResolved ? resolvedPath : request.Path;

        // No candidate matched a tracked file - before falling through to the
        // file-history lookups below (which would raise the generic
        // NoHistoryForPathException), re-run the same flexible-path resolution order
        // with a "is this a tracked directory" predicate instead, so a directory that
        // just needs --recursive gets its own actionable error instead of being
        // conflated with "never tracked at all".
        if (!fileResolved && SnapshotPathResolver.TryResolve(
                session.Profile.TargetRoot,
                path,
                candidate => IsDirectoryTracked(history, candidate),
                session.IsWithinMirror,
                out var resolvedDirectoryPath))
        {
            throw new RestoreTargetIsDirectoryException(resolvedDirectoryPath);
        }

        if (request.At is null && version is null)
        {
            // Neither given, but CanPromptForVersion is true (checked by the caller) -
            // present the path's recorded versions as a selectable list per the
            // snapshot-history delta's "Interactive version selection when
            // restoring" requirement, reusing HistoryTablePresenter's per-row
            // formatting for the prompt's choice labels. A Linked version has no
            // stored content to restore, so it's excluded alongside Deleted.
            var selectable = SelectableVersionsForInteractivePicker(history.GetFileHistory(path));

            if (selectable.Count == 0)
            {
                throw new NoHistoryForPathException(path);
            }

            var selected = environment.ErrorConsole.Prompt(
                new SelectionPrompt<FileVersionRecord>()
                    .Title("Select a version to restore:")
                    .PageSize(10)
                    .UseConverter(v =>
                        $"#{v.Id} - {v.RecordedAt:yyyy-MM-dd HH:mm:ss zzz} - {v.ChangeKind} - " +
                        $"{BackupRunSummaryFormatter.FormatBytes(v.Size)}")
                    .AddChoices(selectable));
            version = selected.Id;
        }

        var effectiveOutPath = request.InPlace
            ? AbsolutePathMirrorMapper.FromMirrorPath(path)
            : request.OutPath!;
        var console = environment.Console;

        void RunRestore(bool overwrite)
        {
            RestoreProgressDisplay.Run(
                console,
                width => BuildRestoreTaskLabel(path, width),
                reporter => RunRestoreOnce(
                    fileVersions,
                    path,
                    version,
                    request.At,
                    effectiveOutPath,
                    overwrite,
                    reporter.OnBytesCopied,
                    reporter.OnSizeResolved));

            OutcomeStyle.WriteLineSuccess(console, $"Restored '{path}' to '{effectiveOutPath}'.");
        }

        try
        {
            RunRestore(request.Force);
            return RestoreOutcome.Completed;
        }
        catch (DestinationExistsException) when (!request.Force && !environment.IsInputRedirected())
        {
            // Interactive session, no --force: ask before overwriting. The prompt goes to
            // stderr so it's still visible even if stdout is redirected. Non-interactive
            // sessions (stdin redirected) fall through and let the exception propagate to
            // ErrorReporting, which reports it as a normal error (exit 1) directing the
            // user to --force.
            var overwrite = environment.ErrorConsole.Confirm(
                $"File '{Markup.Escape(effectiveOutPath)}' already exists. Overwrite?", defaultValue: false);
            if (!overwrite)
            {
                return RestoreOutcome.Declined;
            }

            RunRestore(overwrite: true);
            return RestoreOutcome.Completed;
        }
    }

    // Builds the single-file restore live display's task label from the restored path,
    // pre-truncated to a fixed character budget (per RestoreProgressLabelBudget, derived from
    // the terminal's reported width) before Markup.Escape ever sees it - so Spectre's
    // TaskDescriptionColumn never measures more than a bounded-length string, per design.md's
    // "Pre-truncate the label string instead of a custom column" decision. Internal (rather
    // than private) so RestoreCommandTests can assert the label's bounded length directly,
    // without needing a live-capable console/terminal double to drive the whole progress
    // display.
    internal static string BuildRestoreTaskLabel(string path, int terminalWidth) =>
        BuildRestoreTaskLabel("Restoring '", path, terminalWidth);

    // Shared by both the single-file and recursive directory restore live displays - only the
    // prefix text differs between them (see RunDirectoryRestoreWithLiveDisplay), so both labels
    // are bounded to the same overall budget via RestoreProgressLabelBudget.
    private static string BuildRestoreTaskLabel(string prefix, string path, int terminalWidth)
    {
        const string suffix = "'";
        var budget = RestoreProgressLabelBudget.Compute(terminalWidth);
        var pathBudget = Math.Max(1, budget - prefix.Length - suffix.Length);
        var truncatedPath = PathLabelTruncator.Truncate(path, pathBudget);
        return $"{prefix}{Markup.Escape(truncatedPath)}{suffix}";
    }

    private static void RunRestoreOnce(
        FileVersionService fileVersions,
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
            fileVersions.RestoreVersion(path, version.Value, outPath, overwrite, onBytesCopied, onSizeResolved);
        }
        else
        {
            var asOf = DateTimeOptionParser.Parse("--at", at!);
            fileVersions.RestoreAsOf(path, asOf, outPath, overwrite, onBytesCopied, onSizeResolved);
        }
    }

    // --recursive path: plans the whole directory restore up front (PlanDirectoryRestore is
    // read-only), shows exactly one confirmation covering every planned write/removal (skipped
    // when --force is given), and only then executes - mirroring PruneCommand's
    // count-then-confirm-then-run shape for its own single confirmation, per design.md's
    // "single confirmation replaces per-file overwrite guarding" decision.
    private static RestoreOutcome RunRecursiveRestore(
        HistorySession session,
        RestoreRequest request,
        RestoreEnvironment environment)
    {
        var history = session.History;
        var directoryRestore = session.DirectoryRestore;
        var console = environment.Console;
        var resolution = DirectoryArgumentResolver.Resolve(
            session.Profile.TargetRoot,
            request.Path,
            candidate => IsDirectoryTracked(history, candidate),
            session.IsWithinMirror);
        var resolvedPath = resolution.Path;

        if (resolution.FellBackToMirrorRoot)
        {
            OutcomeStyle.WriteLineNeutral(
                console,
                "The current directory has no recorded history; falling back to the mirror root.");
        }

        // --snapshot resolves directly, bypassing the picker (task 3.2). Otherwise, --at
        // resolves directly as before. When neither is given and the session is interactive,
        // the picker (task 3.3) decides asOf; when neither is given and the session is not
        // interactive, today's default is preserved: silently restore the current state.
        DateTimeOffset? asOf;
        if (request.SnapshotId is not null)
        {
            asOf = directoryRestore.ResolveDirectorySnapshot(resolvedPath, request.SnapshotId.Value);
        }
        else if (request.At is not null)
        {
            asOf = DateTimeOptionParser.Parse("--at", request.At);
        }
        else if (environment.CanPromptForVersion)
        {
            asOf = PromptForDirectorySnapshot(directoryRestore, resolvedPath, environment.ErrorConsole);
        }
        else
        {
            asOf = null;
        }

        var plan = directoryRestore.PlanDirectoryRestore(resolvedPath, asOf, request.OutPath, request.InPlace);

        RestoreOutcome Execute()
        {
            var stoppedEarly = RestoreProgressDisplay.Run(
                console,
                width => BuildDirectoryRestoreTaskLabel(resolvedPath, width),
                reporter => directoryRestore.ExecuteDirectoryRestore(
                    plan,
                    reporter.OnBytesCopied,
                    reporter.OnSizeResolved,
                    environment.CancellationToken));

            if (stoppedEarly)
            {
                // Graceful Ctrl+C stop: whatever was already written/removed stays applied (see
                // ExecuteDirectoryRestore's own cancellation checks) - reported distinctly from
                // a completed restore, in the same warning style BackupOutcomeReporter/
                // PruneOutcomeReporter already use for their own graceful cancellations.
                OutcomeStyle.WriteLinePartialFailure(
                    console,
                    $"Restore of directory '{resolvedPath}' cancelled: stopped early, some planned files may not " +
                    "have been written/removed.");
                return RestoreOutcome.StoppedEarly;
            }

            var skippedClause = plan.Skipped.Count > 0
                ? $", {plan.Skipped.Count} link(s) skipped"
                : string.Empty;
            OutcomeStyle.WriteLineSuccess(
                console,
                $"Restored directory '{resolvedPath}': {plan.ToWrite.Count} file(s) written, " +
                $"{plan.ToRemove.Count} file(s) removed{skippedClause}.");
            return RestoreOutcome.Completed;
        }

        if (request.Force)
        {
            return Execute();
        }

        if (environment.IsInputRedirected())
        {
            // Non-interactive session, no --force: refuse rather than silently writing and
            // removing potentially many files without explicit authorization. Reported as a
            // normal error (exit 1) by ErrorReporting, directing the user to --force.
            throw new RestoreDirectoryConfirmationRequiredException(
                resolvedPath,
                plan.ToWrite.Count,
                plan.ToRemove.Count);
        }

        // Interactive session, no --force: ask once before making any change, covering every
        // planned write and removal - replacing the per-file overwrite prompt single-file
        // restore uses, per the "Single confirmation for a directory restore" requirement. The
        // prompt goes to stderr so it's still visible even if stdout is redirected.
        var removalClause = plan.ToRemove.Count > 0
            ? $" and remove {plan.ToRemove.Count} file(s)"
            : string.Empty;
        var confirmed = environment.ErrorConsole.Confirm(
            $"This will write {plan.ToWrite.Count} file(s){removalClause} under '{Markup.Escape(resolvedPath)}'. " +
            "Continue?",
            defaultValue: false);

        return confirmed ? Execute() : RestoreOutcome.Declined;
    }

    /// <summary>
    /// Presents the directory restore's interactive snapshot picker: a "current tracked
    /// state" entry (pre-selected as the default, so accepting it without changing the
    /// selection reproduces today's plain <c>--recursive</c> default) followed by every
    /// candidate snapshot for <paramref name="directoryPath"/>, most-recent-first, per the
    /// snapshot-history capability's "Interactive and explicit snapshot selection for a
    /// directory restore" requirement. Returns <c>null</c> (the same "current" sentinel
    /// <see cref="RunRecursiveRestore"/> otherwise uses) when the user accepts the default
    /// entry, or the selected candidate's <see cref="Snapshots.Snapshot.StartedAt"/> otherwise.
    /// </summary>
    private static DateTimeOffset? PromptForDirectorySnapshot(
        DirectoryRestoreService directoryRestore,
        string directoryPath,
        IAnsiConsole promptConsole)
    {
        var candidates = directoryRestore.ListDirectorySnapshotCandidates(directoryPath);

        var current = new DirectorySnapshotChoice(null, "Current tracked state");
        var choices = new List<DirectorySnapshotChoice> { current };
        choices.AddRange(candidates.Select(c => new DirectorySnapshotChoice(c, FormatDirectorySnapshotChoice(c))));

        var prompt = new SelectionPrompt<DirectorySnapshotChoice>()
            .Title("Select a point in time to restore:")
            .PageSize(10)
            .UseConverter(choice => choice.Label)
            .AddChoices(choices);
        prompt.DefaultValue = current;

        var selected = promptConsole.Prompt(prompt);
        return selected.Candidate?.Snapshot.StartedAt;
    }

    /// <summary>
    /// One choice in the directory restore's interactive snapshot picker: either the
    /// "current tracked state" sentinel (<see cref="Candidate"/> is <c>null</c>, mapping to
    /// <c>asOf = null</c>) or a specific <see cref="DirectorySnapshotCandidate"/>.
    /// </summary>
    private sealed record DirectorySnapshotChoice(DirectorySnapshotCandidate? Candidate, string Label);

    /// <summary>
    /// Builds a directory snapshot picker candidate's row text: directory-scoped delta counts
    /// (added/changed/moved/deleted) and net byte change, followed by the directory's
    /// point-in-time totals (file count, byte size, and symlink/junction count kept separate
    /// from the file count), per the "Interactive and explicit snapshot selection for a
    /// directory restore" requirement's per-entry reporting. The snapshot's status is colored
    /// via embedded Spectre markup (rather than a <see cref="Spectre.Console.Style"/>-carrying
    /// cell, since <see cref="SelectionPrompt{T}"/>'s converter only produces plain strings),
    /// matching the exact same <c>Complete</c>/<c>Cancelled</c> colors
    /// <see cref="SnapshotsTablePresenter"/> already uses. Every interpolated value here is
    /// either numeric or an enum's own text - never a user-controlled path - so no markup
    /// escaping is needed.
    /// </summary>
    internal static string FormatDirectorySnapshotChoice(DirectorySnapshotCandidate candidate)
    {
        var statusMarkup = candidate.Snapshot.Status switch
        {
            SnapshotStatus.Complete => "[bold palegreen1]Complete[/]",
            SnapshotStatus.Cancelled => "[bold lightgoldenrod2]Cancelled[/]",
            _ => candidate.Snapshot.Status.ToString(),
        };

        var netBytes = candidate.NetBytesDelta >= 0
            ? $"+{BackupRunSummaryFormatter.FormatBytes(candidate.NetBytesDelta)}"
            : $"-{BackupRunSummaryFormatter.FormatBytes(-candidate.NetBytesDelta)}";

        var linksClause = candidate.TotalLinks > 0 ? $", {candidate.TotalLinks} link(s)" : string.Empty;

        return $"#{candidate.Snapshot.Id} - " +
            $"{candidate.Snapshot.StartedAt:yyyy-MM-dd HH:mm:ss zzz} - {statusMarkup} - " +
            $"+{candidate.FilesAdded}/~{candidate.FilesChanged}/" +
            $"\u2192{candidate.FilesMoved}/-{candidate.FilesDeleted} " +
            $"({netBytes}) - {candidate.TotalFiles} file(s), " +
            $"{BackupRunSummaryFormatter.FormatBytes(candidate.TotalBytes)}{linksClause}";
    }

    /// <summary>
    /// Filters a path's recorded history down to the versions offered by the interactive
    /// restore-version picker: a <see cref="FileChangeKind.Deleted"/> entry has no content
    /// at that path, and a <see cref="FileChangeKind.Linked"/> entry is a symlink/junction
    /// with no stored content either (its <see cref="FileVersionRecord.ContentHash"/> is
    /// always <c>null</c>) - both would just error if selected. Internal (rather than
    /// private) so it can be unit-tested directly without simulating an interactive console
    /// session.
    /// </summary>
    internal static IReadOnlyList<FileVersionRecord> SelectableVersionsForInteractivePicker(
        IReadOnlyList<FileVersionRecord> history) =>
        history.Where(v => v.ChangeKind is not (FileChangeKind.DeletedChange or FileChangeKind.LinkedChange)).ToList();

    /// <summary>
    /// Whether any tracked path, at any point in history, falls under <paramref name="candidate"/> -
    /// used as <see cref="SnapshotPathResolver.TryResolve"/>'s "does this candidate match"
    /// predicate for a recursive restore's directory argument, reusing
    /// <see cref="SnapshotHistoryService.ListDirectory"/>'s own "any tracked path" check rather
    /// than duplicating it.
    /// </summary>
    internal static bool IsDirectoryTracked(SnapshotHistoryService history, string candidate)
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

    // Recursive-restore counterpart to BuildRestoreTaskLabel, sharing the same truncation and
    // budget logic - only the prefix text ("Restoring directory '" vs "Restoring '") differs.
    internal static string BuildDirectoryRestoreTaskLabel(string directoryPath, int terminalWidth) =>
        BuildRestoreTaskLabel("Restoring directory '", directoryPath, terminalWidth);
}

/// <summary>
/// Wires a restore's raw <c>onBytesCopied</c> chunk callback and the <c>onSizeResolved</c>
/// callback (which <see cref="FileVersionService"/> and <see cref="DirectoryRestoreService"/> invoke once, right before extraction
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
