## Why

`Program.cs` wires a single Ctrl+C handler (`GracefulCancellation`) and passes its
cancellation token only into `BackupCommand`. `PruneCommand`, `CheckCommand`, and
`RestoreCommand`'s `--recursive` (directory restore) path never receive it, so a first
Ctrl+C during any of those is silently swallowed - `RequestCancellation()` still runs,
but nothing observes the resulting token - and the command keeps running until a second
Ctrl+C hard-kills the process via `Environment.Exit`, with no chance to stop cleanly.
This is most consequential for `prune`, which is actively deleting manifest records and
garbage-collecting content-store blobs when interrupted.

## What Changes

- Thread the existing `GracefulCancellation` token from `Program.cs` into `PruneCommand`,
  `CheckCommand`, and `RestoreCommand`'s recursive-restore path, the same way it already
  reaches `BackupCommand`.
- Each of these commands checks the token between top-level units of work it already
  iterates (e.g. between snapshots considered for pruning, between files verified by
  check, between files/directories processed by a recursive restore) and stops cleanly -
  reporting a cancelled outcome and exiting successfully - instead of continuing to
  completion. This is a simpler, coarser-grained cooperative check than backup's
  mid-run checkpointing; no new checkpoint/resume machinery is introduced.
- A second Ctrl+C during any of these commands continues to force immediate termination,
  exactly as it already does for backup, via the existing `GracefulCancellation` second-signal
  behavior.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `retention-pruning`: `prune` gains a cooperative-cancellation requirement - a single
  Ctrl+C stops the run cleanly before completing all eligible removals/garbage
  collection, rather than being silently ignored.
- `backup-integrity`: `check` gains the same cooperative-cancellation requirement for
  its verification pass.
- `snapshot-history`: `restore --recursive` (directory restore) gains the same
  cooperative-cancellation requirement.

## Impact

- `src/Vara.Cli/Program.cs` (pass the token to the additional commands)
- `src/Vara.Cli/Commands/PruneCommand.cs`, `CheckCommand.cs`, `RestoreCommand.cs`
  (accept a `CancellationToken`, check it between units of work, report a cancelled
  outcome)
- Application-layer services these commands call, to the extent they need to accept and
  observe a token (e.g. the retention/prune pipeline, the check/verification pass, the
  directory-restore executor)
- Existing unit/integration tests for these commands, plus new tests covering
  first-Ctrl+C-stops-cleanly and second-Ctrl+C-forces-exit for each
