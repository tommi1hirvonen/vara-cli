## Context

See proposal.md for why. This design builds on `fsharp-backup-presentation`, which is applied before it, and reuses what that change established instead of defining it again.

What is in place, and what this design uses:

- **Domain** (`Vara.Domain`): `ManifestRow`, `RowContent`, `ChangeKind`, `MirrorPath`, `TrackedPaths`, `SnapshotStatus`, `Outcome`, `Counts`. Presentation: `Display` (lines, fixed-width cells, `Tone`), `Tracker` and `ProgressEvent` (with `TransferStarting(totalBytes, totalFiles)`, `BytesCopied`, `FileTransferred`, `RunEnded`), `Throttle`, `ByteSize`, `Elapsed`, `PathLabel.truncate`, `OutputMode`, `Capabilities`, `TerminalProgress`, `JsonValue`.
- **Engine** (`Vara.Engine`): `SqliteManifest` (opened read-only for a dry run through `openAt path false`), `ContentStore` (layout, placement, `Links`), `Kernel32.getFinalPathName` (real path through any reparse point), `Streams` (a copy with a byte callback), `RunLock`, `Flow` (`attempt`, `rethrow`, `foldChannel`), `ProfileResolution`, `PowerRequest`.
- **Cli** (`Vara.Cli`): `CliEnv` (the record of effects), `CommandAction.set`, `ErrorReporting`, `GracefulCancellation`, `RunProgress.withProgress` and its `Screen`, `ProgressDisplay.show`, `LiveRenderer`, `PlainRenderer`, `Terminal`, `JsonOutput`. The profile is resolved from the working directory by `fsharp-profile-config`.
- **Tests**: `Vara.Testing.SpecCoverage` and the per-assembly coverage tests in `Vara.Engine.Tests`, the process-based parity pattern in `Vara.Cli.Tests/CommandParityTests.fs`, `TestConsole` driven through `CliEnv.Capabilities`.

The C# code is the behavioral reference, not a design: `DirectoryRestoreService`, `RestoreCommand`, `BrowseCommand`, `RestoreProgressDisplay`, `RestoreProgressLabelBudget`, `PathLabelTruncator` (already ported as `PathLabel`) and the snapshot and history query services.

## Goals / Non-Goals

**Goals:**

- Every owned scenario is verified by a test named after it. Decisions are tested on plain data (rows in, a value out) before any file or console is involved.
- Restore writes are safe by construction: planned as data, checked against the live mirror before anything is written, and written atomically.
- Restore progress is the backup's `Tracker` and `Display`, not a second progress model.
- Output parity with C# where it can be compared mechanically: restored files and bytes, exit codes, and the text of listings.

**Non-Goals:**

- A `--json` mode for these commands. The spec scopes it to `backup`.
- Retention, integrity and profile editing (later changes).
- Any change to the history spec. A behavior difference found while porting is recorded as a C# bug fix with a parity test first.

## Decisions

### D1. Each command is the same pipeline with a different projection

`resolve profile -> open the manifest read-only -> query rows -> project (pure) -> Display -> render`. The shared stages are functions in `Vara.Cli` (`History.resolve`, `History.withManifest`); a command module composes them with its own projection and does not extend a base. The pure projection of every command has the type `rows -> request -> Display` (or `Result<Display, HistoryError>` when the request can name something unknown). The manifest is opened read-only for every command except the restore's lock and the writes it makes to the destination, which are not manifest writes.

Alternatives: a `HistoryService` per capability (rejected by the series rules, and it would repeat the C# structure); passing a manifest object into the domain (rejected: the domain takes rows as values).

### D2. The queries return rows and the domain decides

`SqliteManifest` gains a handful of query functions that return `ManifestRow` sequences: rows of a path, rows of a snapshot, rows under a directory up to a snapshot, the snapshots themselves. They contain filters and ordering that SQL does well and no decision. `Vara.Domain.History` holds the decisions as folds over the rows passed in:

- `History.stateAt : Moment -> rows -> TrackedState voption` (the version live at a snapshot or instant);
- `History.versionsOf : rows -> Version list` (a file's history, following the move chain from the current path back through its earlier paths);
- `History.listing : Moment -> directory -> bool -> rows -> Entry list` (one level, deleted entries optionally included);
- `History.rollup : rows -> Rollup` (counts and entries per change kind for one snapshot);
- `History.recentlyDeleted : rows -> DeletedEntry list`.

A `Moment` is a union (`AtSnapshot of int64 | AtTime of DateTimeOffset | Latest`), so the commands cannot confuse an id with a time. Per-row values stay struct unions where a fold touches every row, as in the engine.

### D3. A restore is a plan, then an execution

`Restore.plan : RestoreRequest -> rows -> Result<RestorePlan, HistoryError>` is pure. A `RestorePlan` holds the writes (blob to destination, with size and the modification time to restore), the removals (files that did not exist at the requested time, for a directory restore), and the totals the progress needs (bytes and files to write; removals count for neither). The guard (D4) and the single confirmation of a directory restore read the plan. A preview is the plan rendered as a `Display`. The execution in `Vara.Engine.Restore.execute` takes the plan, an `ExecutionEnv`-style record of functions (copy with a byte callback, move, delete, clock, `ShouldStop`) and returns a `RestoreResult` of counts and failed paths. A per-file I/O failure is a value (`Failed path`) that the restore records and continues past; anything else is an exception, rethrown from the shell with its stack, as in the backup. This keeps the three failure levels the backup established.

### D4. The live mirror is protected by a pure check over a resolved path

The shell resolves the destination's real path with `Kernel32.getFinalPathName` (through junctions and symlinks) and passes the string to `Restore.guard : mirrorRoot -> resolvedDestination -> Result<unit, HistoryError>`, which is a pure prefix test on canonical paths. The effect (resolving) and the decision (inside the mirror or not) are separate, so the decision is tested with strings, including trailing separators, case and `..`.

Atomic writes: each file is copied to a temporary name in the destination's own directory, flushed, and moved over the destination with overwrite. A failure or a cancellation leaves the previous file untouched and removes the temporary. The temporary name never collides with a mirror path because the destination is never inside the mirror.

### D5. Restore progress reuses the backup's tracker and display

- The restore starts in the transfer phase: it publishes `TransferStarting(planBytes, planFiles)` first, then `BytesCopied` and `FileTransferred` as the execution copies. There is no scan phase, so the bar starts at 0% against a known total, which is the spec's "no indeterminate phase".
- `RunProgress.withProgress` today takes `Observers -> Task<'a>`, which is the backup engine's record. It is changed to take `(ProgressEvent -> unit) -> Task<'a>`, and the backup maps the events to `Observers` in one function (`BackupCommand`). The restore maps them to its own callbacks. The bracket, the heartbeat, the closed flag and the terminal indicator are shared.
- `Display.view` hardcodes the scan and transfer labels. It takes a `Wording` record (the two labels) instead. The backup passes its labels; the restore passes a label built from the destination with `PathLabel.truncate` at a budget of `Display.labelWidth - 1`. The label column therefore keeps its width and the bar keeps its length however long the path is, which is the "Restore task labels are bounded to a fixed width" requirement, tested on `Display` values.
- "Restoring a small file does not flash": a pure `ProgressDisplay.worthShowing totalBytes` decides before the live region starts. A restore whose total is below the threshold is shown as the summary only. The threshold is a named constant taken from the C# build and commented; the C# value is read in the preparation task.
- Aggregate progress for a directory restore is the same single `Tracker`, over the sum of the plan's writes; removals publish nothing, so they cannot distort throughput or ETA.
- Plain and redirected output appends `Display.plainProgress` lines as the backup does.

Alternative: a second, restore-specific progress model with its own columns (as C# has). Rejected: it duplicates the rate limit, the never-regressing byte count and the heartbeat, which are the parts that were hard to get right.

### D6. Prompts are a record of two functions, built at the edge

Interactive selection (a file's version, a directory's snapshot) and the single confirmation need input. `CliEnv` gains `Prompts: { Select: string -> string list -> int voption; Confirm: string -> bool }`, and `Capabilities` gains `InputIsInteractive`. The Spectre prompts live in one module (`Prompts`), the only place that calls them, and the commands never see Spectre. The pure part is the candidate list: `History.restorableVersions` excludes deleted versions and symlink versions, as the spec says. A test passes scripted prompt functions. When the session is not interactive and nothing was given, the command reports the missing selection as an error, as C# does. `fsharp-profile-management` reuses `Prompts` for its editor.

AOT: Spectre prompts must publish without IL warnings. The first task of the change publishes a probe with a selection prompt, so a problem surfaces before the commands depend on it.

### D7. Date/time and path input are parsers with friendly errors

`DateInput.parse : now -> offset -> string -> Result<DateTimeOffset, DateInputError>` and `DateInput.describe : DateInputError -> string` are pure; the error text names the option and includes an example value, and the same example is the help text of every date/time option (`DateInput.example`, built from one constant so that help and error cannot differ). Options are declared as strings and parsed in the handler, so the message is Vara's and not System.CommandLine's. `PathInput.resolve` handles the flexible forms (relative to the working directory, absolute, with or without the mirror prefix) as a pure function from text and a base to a `MirrorPath` or an error that names the path.

### D8. Diffs and boxes are `Display` data

A diff is built as lines with `Tone`s (added, removed, context) by a pure function over two versions' text. DiffPlex (pinned centrally) is used in `Vara.Cli` if it publishes without IL warnings; the probe in the first task decides. If it does not, a small line diff in `Vara.Domain` replaces it, and DiffPlex is not added. "Bordered widgets use a consistent rounded-corner style" is a `Line.Box` case of `Display` (a title and the lines inside it), drawn by `LiveRenderer` with Spectre's rounded border and by `PlainRenderer` as indented text. Tests assert the box on `Display` values, and the rounded border on the live renderer's output.

### D9. Cancellation and exit codes follow the backup

The directory restore checks `ShouldStop` between files, as the backup does between operations, finishes the file in progress, and returns a `RestoreResult` that the command maps to `Outcome.PartialFailure`. `Display.toneOf` and `Outcome.exitCode` give the style and the exit code for every command in this change. A hard error is an exception handled by `CommandAction.set`, as before.

### D10. Parity is checked on files and text, in processes

The parity tests run the C# and F# commands as processes on copies of the same target and compare the restored files byte for byte (and their modification times), exit codes, and the text of listings after normalizing volatile fields. Interactive flows are tested with scripted prompts on the F# side only, because a child process cannot be driven through a prompt. A graceful Ctrl+C cannot be delivered to a child process either, so the cancelled directory restore is tested in process on the F# side and its C# behavior is read from the code and recorded.

## Risks / Trade-offs

- [DiffPlex or Spectre prompts are not AOT-clean] → The first task publishes a probe that uses both. The fallback for the diff is a small pure line diff; the prompts have no fallback, and a failure is reported before any command is built on them.
- [A restore writes into the live mirror through a junction or a different path spelling] → The decision is a pure test on a real resolved path, covered by tests with junctions, symlinks, trailing separators and case; the check runs before the first write and again for each directory of a directory restore.
- [The restore plan for a large directory holds every write in memory] → The plan is a list of small records; a hundred thousand writes is a few megabytes. The execution streams through it and holds no content.
- [Restore progress differs visibly from C#, which has percentage, remaining-time and speed columns in one line] → The F# restore shows the backup's bar and statistics rows. The spec asks for a byte-based indicator that advances, with a known total, without a flash, and a plain fallback; none of that names the columns. Recorded as a deliberate difference.
- [The live manifest is read while a backup writes it] → Reading is read-only and the history commands take no run lock, as in C#; a snapshot that is `Running` is shown as such and the spec's wording is followed.
- [Time-dependent behavior (dates without a time zone, daylight saving) differs between the builds] → `DateInput.parse` takes the offset as a parameter, so tests fix it; the parity tests run both builds in the same time zone.

## Migration Plan

Nothing ships: the C# `vara.exe` stays the distributed build. To roll back, revert this change's commits. The change to `RunProgress.withProgress` and `Display.view` alters code that `fsharp-backup-presentation` added; its tests (the backup's progress and parity tests) must stay green and are rerun in the final task.

## Open Questions

- Whether the live restore display needs the files statistics row for a single-file restore (`Files 0 / 1`). It follows from reusing `Display.view` and is left as is unless it reads badly in a terminal; deciding does not change the tasks.
