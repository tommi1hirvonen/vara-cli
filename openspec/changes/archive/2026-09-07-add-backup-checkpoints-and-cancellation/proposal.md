## Why

A backup run currently groups every manifest write into a single transaction spanning the entire run, and registers no handler for Ctrl+C. Interrupting a run - whether by process kill, power loss, or a user pressing Ctrl+C - discards the whole transaction, so a multi-hour first backup that is interrupted near the end must re-hash and re-transfer everything, not just the recently-changed portion. This is self-healing (no corruption), but disproportionately expensive for large first runs to a removable or external drive, which are also the runs most likely to be interrupted mid-way.

## What Changes

- Replace the single whole-run manifest transaction with periodic checkpoint commits (bounded by elapsed time), so an interruption only requires re-recording work performed since the last checkpoint instead of the entire run.
- Add a `Console.CancelKeyPress` handler so Ctrl+C requests a graceful stop instead of an abrupt process kill: in-flight file operations are allowed to finish, an out-of-cycle checkpoint commit is forced immediately, and the process then exits deliberately rather than being torn down by the OS.
- Record a Ctrl+C-interrupted run under a new `Cancelled` snapshot status, distinct from `Failed`, so history/listing output can tell a deliberate stop apart from a crash.
- Map the new `Cancelled` status to the existing "success with partial failures" (warning) severity style for display and exit-code purposes, rather than introducing a new severity tier.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `backup-execution`: the "Manifest writes are batched per snapshot" requirement changes from one commit per run to periodic checkpoint commits with a bounded redo window; a new requirement covers graceful Ctrl+C cancellation (forced checkpoint, deliberate exit, `Cancelled` snapshot status).
- `snapshot-history`: snapshot listing/history output must recognize and display the new `Cancelled` status distinctly from `Failed`.

## Impact

- `Vara.Application.Backup.BackupPipeline` / `BackupExecutor`: checkpoint scheduling during the transfer loop, and a cancellation signal threaded through the run so in-flight work can wind down before the forced checkpoint.
- `Vara.Infrastructure.Snapshots.SqliteSnapshotRepository` / `IManifestBatch`: support committing and reopening the batch transaction mid-run instead of a single begin/commit pair; new `Cancelled` value in the snapshot status enum/schema.
- `Vara.Cli.Program`: register a `Console.CancelKeyPress` handler at startup.
- Snapshot listing/history commands (`Vara.Cli.Commands`) and their severity-style mapping (`cli-presentation` conventions, referenced but not modified).
- No changes to on-disk mirror layout, content-addressing, or existing manifest columns beyond the new status value.
