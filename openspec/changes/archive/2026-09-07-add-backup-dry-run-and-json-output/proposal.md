## Why

`vara backup` has no way to preview what a run would do without actually doing it, and no machine-readable output for scripting/automation. A user who wants to know "how many files would change" before committing to a run - or a script that wants to check a run's outcome without parsing colored, human-oriented text - currently has no supported way to do either.

## What Changes

- New `--dry-run` option on `vara backup`: performs the scan/diff/plan stages exactly as a real run would (so reported counts reflect real move detection, exclude/glob filtering, etc.), but performs no writes at all - no mirror writes, no manifest writes, no new snapshot record - and reports the planned added/changed/moved/deleted counts and total bytes that would be transferred instead. It still acquires the run lock for the duration of planning, so its output reflects a consistent view and it does not race a concurrently starting real run.
- New `--json` option on `vara backup`, usable with or without `--dry-run`: prints a single machine-readable JSON summary of the run's (or dry run's) outcome to stdout instead of the human-oriented colored summary, and suppresses the interactive progress display, so the command is scriptable without parsing formatted text.
- Not BREAKING: both options are opt-in and additive; `vara backup`'s behavior and output are unchanged when neither is given.
- `--json` and `--dry-run` are scoped to the `backup` command only for now, not introduced as a CLI-wide convention.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: adds a dry-run mode that reports a backup run's planned changes without performing any writes.
- `cli-presentation`: adds a machine-readable JSON output mode for the `backup` command's run summary, scoped to that command only.

## Impact

- `src/Vara.Application/Backup/BackupPipeline.cs` (new plan-only entry point, no snapshot/manifest writes)
- `src/Vara.Cli/Commands/BackupCommand.cs` (`--dry-run`, `--json` options; JSON serialization path; progress suppression under `--json`)
- New result/summary type(s) for the dry-run outcome, and a JSON-serializable shape covering both dry-run and executed outcomes (see design.md)
- `tests/Vara.Application.Tests/Backup/BackupPipelineTests.cs`, `tests/Vara.Cli.Tests/Commands/*BackupCommand*`
