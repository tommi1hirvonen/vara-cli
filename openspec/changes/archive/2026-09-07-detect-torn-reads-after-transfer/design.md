## Context
`BackupExecutor.ExecuteTransfer` opens `operation.SourceAbsolutePath`, streams it through
`StreamingContentSignature` to compute the quick hash and full content hash, stores it via
`contentStore.StoreFromStream`, places it at the mirror path, and then calls
`repository.RecordFileVersion` with `operation.SourceModifiedAt` - a value carried unchanged from
the scan phase (`DirectoryFileSystemScanner`/`BackupPlanner`) all the way through planning into
execution. Scanning and transfer are separate phases of the same run (scan produces the whole
`BackupPlan` before any transfer starts), so an arbitrary amount of time can pass between when a
given file was scanned and when its content is actually read here. `size` recorded alongside it
*is* accurate - it comes from `StoreFromStream`'s return value, reflecting exactly what was just
read - only the modification time is carried from the earlier scan.

## Goals / Non-Goals

**Goals:**
- Detect, immediately after a file's content is read and stored, whether the source file's size
  or modification time has since diverged from what the scan observed.
- When divergence is detected, avoid ever recording a manifest entry that understates how stale
  its `SourceModifiedAt` is relative to the content actually captured.
- Guarantee such a file is naturally re-selected by the next run's incremental change detection.

**Non-Goals:**
- Detecting a true byte-level torn read (a write interleaved *during* the read itself, producing a
  hybrid blob) by any means other than the before/after stat comparison. A stat-based check cannot
  distinguish "read a fully torn, hybrid blob" from "read cleanly, then the file changed a moment
  later" - both produce a stat mismatch, and both are handled identically (treated as failed),
  which is sufficient: either way, the content isn't trustworthy as "the file as of this recorded
  timestamp," and re-running will correct it.
- Retrying the read within the same run. Failing the operation and letting the next run's normal
  incremental scan pick it up again is sufficient and consistent with how every other per-file
  transfer failure is already handled.
- Changing what gets recorded when the file is unchanged (the overwhelmingly common case) -
  behavior there is identical to today.

## Decisions
- **Fail the operation on any stat mismatch, rather than re-recording the fresh post-read stat.**
  Re-recording the fresh mtime was considered: capture a new `FileInfo` after the read and use
  its `LastWriteTimeUtc` instead of `operation.SourceModifiedAt` when writing the manifest entry.
  Rejected because it can still misrepresent reality - if the file was mid-write when read
  (a genuine torn read), the freshly-observed post-read mtime does not necessarily correspond to
  the exact bytes actually captured (the write may still be in progress, or may complete a moment
  after the second stat). Treating the whole operation as failed avoids ever asserting a
  content-to-metadata pairing the system cannot actually vouch for, at the cost of one extra
  backup cycle before the file's genuinely latest content is captured - an acceptable trade given
  "Unreadable files do not abort the run" already establishes that per-file failures are a normal,
  expected outcome the system reports and recovers from on the next run.
- **Compare both size and modification time**, not modification time alone, for symmetry with how
  incremental change detection itself decides a file changed (`backup-execution`'s "Incremental
  change detection" requirement already compares both).
- **Re-check with a plain `FileInfo` stat**, the same mechanism the scanner itself uses
  (`ScannedEntry`/`ToEntry` in `DirectoryFileSystemScanner`), for consistency and to avoid
  introducing a second way of reading file metadata.
- **Treat the mismatch exactly like an `IOException`/`UnauthorizedAccessException`** (increment
  `Counts.Failed`, add to `failedPaths`, skip the manifest write) rather than inventing a new
  failure category - the existing failure-handling and reporting path already does the right
  thing (continue the run, surface the failed path, leave prior manifest state untouched).

## Risks / Trade-offs
- [Risk] A file that is being actively, continuously rewritten (for example a database or log
  file under active use) could fail every single backup run, never successfully capturing a
  version.
  -> Mitigation: this is an accurate reflection of reality - such a file's content is genuinely
  unstable, and today's behavior (recording it under a stale timestamp) is a wrong-but-quiet
  version of the same underlying problem. The path still shows up in the run's failure report
  each time, giving the user visibility rather than an unnoticed inconsistency. Handling that
  class of file better (e.g. via a "in-use file" retry policy or a hint like VSS) is out of scope
  here.
- [Risk] The extra `FileInfo` stat call after every transferred file adds a small amount of I/O.
  -> Mitigation: negligible relative to the file read/hash/store/mirror-write work already done
  for every transferred file in the same code path.
