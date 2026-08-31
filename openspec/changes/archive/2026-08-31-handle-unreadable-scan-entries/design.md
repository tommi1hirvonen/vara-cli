## Context

See proposal.md - Why. Relevant existing code, confirmed by direct inspection:

- `DirectoryFileSystemScanner.Walk`'s outer catch around
  `Directory.EnumerateFileSystemEntries` already catches
  `UnauthorizedAccessException or IOException`, but discards the result via
  `yield break` with nothing recorded.
- The per-entry `File.GetAttributes` call inside `Walk`'s loop, the
  `File.GetAttributes` call in `ToEntry`, and `FileInfo.LinkTarget` in
  `TryGetLinkTarget` each catch only `IOException`.
- `IFileSystemScanner.Scan` returns `IEnumerable<ScannedEntry>` - there is no
  channel today for a scan-time failure to travel alongside successfully scanned
  entries.
- `BackupExecutor` already has the pattern this change follows: per-file transfer
  failures are caught (`IOException or UnauthorizedAccessException`), added to a
  `failedPaths` list, and folded into `SnapshotStats.FilesFailed` /
  `BackupRunResult.FailedPaths`. Scan-time failures should join the same reporting
  path rather than inventing a parallel one.
- `ErrorReporting.TryGetFriendlyMessage` whitelists five domain exceptions; anything
  else escapes as an unhandled exception. This change does not add to that
  whitelist - the fix is to stop the exception from reaching that far at all.

## Goals / Non-Goals

**Goals:**
- Guarantee that no exception thrown while reading a single entry's metadata
  during scanning can escape `IFileSystemScanner.Scan` and crash the run.
- Make scan-time skips (unreadable file or unenumerable directory) visible in the
  same result data the executor already uses for per-file failures, so a user
  looking at run output cannot mistake a partial run for a complete one.

**Non-Goals:**
- Retrying, elevating privileges, or otherwise attempting to actually read a
  permission-denied entry. The behavior is still "skip and record," not "recover."
- Changing `ErrorReporting`'s exception whitelist or CLI-level error formatting.
- Distinguishing *why* a directory couldn't be enumerated (ACL vs. other I/O
  failure) in the reported result - both are recorded as a scan failure the same
  way `BackupExecutor` doesn't distinguish failure causes for transfer failures.

## Decisions

**1. Widen the exception filter at every per-entry metadata read site to match
`BackupExecutor`'s existing pattern (`IOException or UnauthorizedAccessException`).**
This is the minimal, consistent fix for the crash bug: `Walk`'s per-entry
`File.GetAttributes`, `ToEntry`'s `File.GetAttributes`, and
`TryGetLinkTarget`. Alternative considered: wrap the whole `Scan` method body in a
single broad try/catch. Rejected - it's an iterator method (`yield return`), so a
method-level try/catch can't wrap the parts that yield, and it would blur exactly
which entry failed, which the reporting goal needs.

**2. Represent a scan-time failure as a new lightweight result alongside
`ScannedEntry`, e.g. a `ScanFailure` record with a relative path and a reason
(`UnreadableEntry` vs `UnreadableDirectory`), and change `IFileSystemScanner.Scan`
to return a type carrying both successful entries and failures (or emit failures
through a second `IEnumerable<ScanFailure>`).**
Alternative considered: throw a soft/marker exception per failure and have the
diff/plan stage catch it. Rejected - failures aren't exceptional control flow for
the caller, they're expected data, and threading them as return data keeps
`Scan`'s existing lazy `IEnumerable<ScannedEntry>` shape intact for the happy path.
Alternative considered: log failures via `ILogger`/console directly from the
scanner. Rejected - the scanner has no dependency on logging or reporting
infrastructure today, and per-file failures already flow through
`BackupRunResult`/`SnapshotStats` rather than ad hoc logging, so scan failures
should follow the same path for consistency.

**3. Fold scan-time failures into the same counters/lists `BackupExecutor` already
produces (`SnapshotStats.FilesFailed`, `BackupRunResult.FailedPaths`) rather than
adding a separate summary field.**
`BackupPipeline.Run` collects scan failures alongside the scanned entries, then
merges them with the executor's own failures before building `SnapshotStats` and
`BackupRunResult`. This keeps "how many things failed this run" a single number a
caller checks, instead of two separate failure lists a caller could forget to
check.

## Verified limitation (found during implementation)

Direct experimentation (applying an explicit ACL deny via `icacls`, up to and
including a full deny, for the current user on a test file) showed that
`File.GetAttributes`, `FileInfo.Length`, `FileInfo.LastWriteTimeUtc`, and
`FileInfo.LinkTarget` do **not** throw `UnauthorizedAccessException` for a
per-file ACL deny on Windows - these are lightweight metadata queries that do not
enforce the target file's own DACL, only the containing directory's
list/traverse rights. Only actually opening file content (e.g. `File.OpenRead`,
already covered by `BackupExecutor`) enforces the per-file ACL.

`Directory.EnumerateFileSystemEntries` **does** enforce the ACL and reliably
throws `UnauthorizedAccessException` for a directory the caller cannot list
(verified the same way, and matches the well-known real-world case of a
non-admin listing `System Volume Information`) - this is exactly the path
Decision 3 and task 2.4 target, and it is covered by a real, passing test.

Net effect: the per-entry catch widening in `Walk`/`ToEntry`/`TryGetLinkTarget`
(Decision 1) is kept as defensive coding - it is still correct and consistent
with `BackupExecutor`'s existing pattern, and may matter in environments where
these APIs behave differently (e.g. non-NTFS filesystems, different OS
versions/policies) - but it is not reproducible, and therefore not asserted, via
ACL manipulation in a test on this platform. Test coverage for "unreadable file"
is consequently scoped to the directory-enumeration case only; there is no
per-file ACL-deny test.

## Risks / Trade-offs

- [Risk] Changing `IFileSystemScanner.Scan`'s signature/return shape is a breaking
  change to an internal abstraction. -> Mitigation: `IFileSystemScanner` is
  internal to this codebase (no external consumers); update the single production
  implementation (`DirectoryFileSystemScanner`) and its tests in the same change.
- [Risk] A permission-denied directory near the root of a large source tree could
  now report a large number of "failed" paths if failures are recorded per-file
  instead of per-subtree. -> Mitigation: record one failure per unenumerable
  directory (the subtree root), not one per file that would have been inside it -
  matches how the requirement is worded ("subtree was skipped") and avoids
  needing to enumerate a tree it just failed to enumerate.
- [Risk] Broadening the caught exception types could mask a genuinely unexpected
  bug (e.g. a real permissions misconfiguration a user would want to know about
  loudly). -> Mitigation: the failure is still recorded and surfaced in the run
  result/summary, just not as a crash - visibility is preserved, only the failure
  mode changes from "hard crash" to "reported skip," consistent with the
  existing per-file behavior in `BackupExecutor`.

## Open Questions

- Exact surface for reporting scan failures to the end user (CLI summary line vs.
  only in `BackupRunResult` for now) is left to tasks/implementation - it doesn't
  change the spec requirement or the chosen approach, since the spec only requires
  the run result to record the skip, not a specific CLI presentation.
