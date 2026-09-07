## Why
`BackupExecutor.ExecuteTransfer` reads and hashes a source file's current bytes, but records the
resulting manifest entry's modification time using `operation.SourceModifiedAt` - the value
captured during the earlier scan phase, not the file's actual state at the moment it was read. If
the file is edited between when it was scanned and when its content is actually streamed and
hashed during transfer (a real possibility: scanning and transfer are separate phases, and a large
backup can take a long time to reach a given file), the manifest ends up pairing correctly-read
content with a stale, no-longer-accurate modification time. Depending on the specific timing of a
later, unrelated edit, this stale pairing can prevent the next run's size+modification-time diff
from ever detecting that the file needs to be re-examined, silently leaving its backed-up content
out of date.

## What Changes
- After transferring a file's content (reading, hashing, and storing it), re-`stat` the source
  file and compare its current size and modification time against the values captured at scan
  time.
- If they match, record the manifest entry exactly as today.
- If they differ - meaning the file was modified after it was scanned and before (or while) it was
  read - treat the operation as failed for this run, the same way an `IOException`/
  `UnauthorizedAccessException` is already handled: increment the failure count, record the path
  in the run's failed-paths list, and do not write a manifest entry or place the content at the
  mirror path. This guarantees the file is picked up again on the next run's scan (its on-disk
  state will still differ from its last successfully recorded manifest state), rather than being
  recorded now under metadata that doesn't describe what was actually captured.

## Capabilities

### Modified Capabilities
- `backup-execution`: adds a requirement that a file modified between scan and transfer is not
  recorded under stale modification-time metadata; it is instead treated as a failed operation for
  this run and retried on the next.

## Impact
- `Vara.Application.Backup.BackupExecutor` (`ExecuteTransfer`).
- A file edited during its own backup's transfer window is now reported as a per-file failure for
  that run (visible in the run's failure count/paths) instead of silently succeeding with
  inaccurate metadata; it is automatically retried on the next run.
- No change for the overwhelmingly common case where a file is not modified during its own
  transfer window.
