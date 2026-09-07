## Why

`backup` exits `0` even when one or more files failed (locked, permission-denied,
etc.). `ErrorReporting.Run` returns `0` for any action that doesn't throw, and a
partial failure never throws - `BackupCommand` only changes the on-screen color
(amber "partial failure" vs green "success") via `BackupOutcomeReporter`, never the
process exit code. A scheduled/unattended run (Task Scheduler, cron, a script
checking `$LASTEXITCODE`) therefore cannot detect that files were skipped; it only
sees a clean, successful exit. An audit of the other commands (`prune`, `restore`,
`diff`, `history`, `snapshots`, `browse`, `deleted`) found no equivalent gap - `backup`
is the only command with a "completed, but some items failed" result shape
(`BackupRunResult.FailedPaths`) that isn't already surfaced through an exception.

## What Changes

- Introduce a distinct, documented exit code for "the command completed but one or
  more items failed" (`2`), alongside the existing `0` (full success) and `1` (hard
  error) codes, so a caller can distinguish "some files were skipped" from both a
  clean run and a run that didn't complete at all.
- Wire `BackupCommand` to return that new exit code when `BackupRunResult.FailedPaths`
  is non-empty, instead of always returning whatever `ErrorReporting.Run` returns (`0`,
  since a partial failure never throws).
- No other command changes behavior: none of them currently has a "completed with
  partial, non-throwing failures" result to surface.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `cli-presentation`: the outcome-severity taxonomy (success / partial failure / hard
  error) already rendered visually now also determines the process exit code, adding
  a new "partial failure" exit code distinct from the existing hard-error exit code.

## Impact

- `src/Vara.Cli/Commands/BackupCommand.cs`: capture the `BackupRunResult` out of the
  `ErrorReporting.Run` action and override the returned exit code when
  `FailedPaths.Count > 0`.
- `src/Vara.Cli/Composition/ErrorReporting.cs` (or a small shared constants location):
  define the new exit code value alongside the existing `0`/`1` contract.
- No change to `prune`, `restore`, `diff`, `history`, `snapshots`, `browse`, or
  `deleted` - confirmed none has an equivalent silent partial-failure result today.
- No change to on-screen rendering (`BackupOutcomeReporter` styling stays as-is) -
  this only changes the process exit code returned after that output is written.
