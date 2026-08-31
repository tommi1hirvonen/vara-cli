## Context

`BackupExecutor.Execute` runs Move/Delete operations sequentially in a pre-loop
(`ExecuteMoveOrDelete`, no try/catch) before running Add/Change operations in
parallel (`ExecuteTransfer`, which catches `IOException`/`UnauthorizedAccessException`
per operation and records the path to `failedPaths`). `IContentStore.MoveMirrorEntry`
(`File.Move`) and `RemoveFromMirror` (`File.Delete`) can throw those same exception
types when the target mirror entry is locked or inaccessible. Because
`ExecuteMoveOrDelete` has no catch, such an exception propagates out of `Execute`
into `BackupPipeline.Run`'s outer `catch`, which marks the whole snapshot Failed and
rethrows - aborting the run for what should be a single recorded failure. See
proposal.md for the full motivation.

## Goals / Non-Goals

**Goals:**
- Make a locked/inaccessible mirror entry during Move or Delete degrade to a
  per-path recorded failure, matching the existing Add/Change behavior.
- Leave manifest state consistent when a Move or Delete fails partway - never write
  a `RecordFileVersion` entry that implies an operation succeeded when it didn't.
- Keep the fix localized to `BackupExecutor`; no change to `IContentStore`'s
  interface or the underlying `File.Move`/`File.Delete` calls.

**Non-Goals:**
- Retrying locked operations (e.g. wait-and-retry on `IOException`). Out of scope;
  can be a future enhancement.
- Changing the sequential-vs-parallel execution split between Move/Delete and
  Add/Change - only fault isolation is being added, not concurrency behavior.
- Handling exceptions beyond `IOException`/`UnauthorizedAccessException` (e.g.
  `PathTooLongException`) - matches the existing `ExecuteTransfer` scope; broader
  exception handling is a separate concern.

## Decisions

**Wrap the body of `ExecuteMoveOrDelete` per-operation, mirroring `ExecuteTransfer`'s
catch clause exactly (`catch (Exception ex) when (ex is IOException or
UnauthorizedAccessException)`).** This keeps the two code paths symmetric and
reviewable side by side, rather than introducing a different failure-handling
idiom for Move/Delete.

**Order of operations inside the try: perform the content-store call
(`MoveMirrorEntry`/`RemoveFromMirror`) first, then the `RecordFileVersion` call(s),
all inside the same try.** If the content-store call throws, no manifest write
happens for that operation - the manifest continues to reflect the pre-run state
for that path, which is safe and consistent (the path will be re-evaluated as
changed on the next run, same as any other skipped file). Alternative considered:
catching only around the content-store call and always writing the manifest
records regardless of outcome - rejected because it would record a Moved/Deleted
version for a file that was never actually moved/deleted in the mirror, corrupting
history.

**Move failures are recorded as a single failed path (the operation's current
`RelativePath`), not split into a separate "old path" and "new path" failure.**
The move either succeeds as a unit or it doesn't; recording one path keeps
`FailedPaths` reporting simple and consistent with how Add/Change already reports
one path per failed operation. Alternative considered: recording both
`PreviousRelativePath` and `RelativePath` as separate failures - rejected as noise,
since it's a single logical operation that failed, not two.

**Move `failedPaths`/`reportLock`/`counts.Failed` plumbing into `ExecuteMoveOrDelete`
the same way it's already passed into `ExecuteTransfer`.** Since Move/Delete run
sequentially (not inside the `Parallel.ForEach`), the lock isn't strictly required
for thread-safety there, but reusing the same `Counts`/`failedPaths` instances (not
a separate lock) keeps a single shared accumulation point that `Execute` assembles
into the final `ExecutionOutcome`. No new lock is introduced for the sequential
loop; the existing `reportLock` object is only actually contended by the later
parallel phase.

## Risks / Trade-offs

- **[Risk] A move failure leaves a file present at both the old and new mirror
  paths in a subsequent run if the source's move is later retried.** This is
  already the existing behavior on any skipped file (same as an unreadable source
  file) - the next run's diff will simply see the source file as unchanged from
  its current mirror location, or reconcile naturally. No new inconsistency is
  introduced beyond what "unreadable files do not abort the run" already accepts
  for other skip cases. → Mitigation: none needed beyond existing recorded-failure
  visibility to the user.
- **[Risk] Swallowing the exception could mask a systemic problem (e.g. the whole
  target volume is offline), turning one loud failure into many silent per-file
  failures.** → Mitigation: none added in this change; matches the existing
  Add/Change behavior, which has the same characteristic and relies on the
  aggregated `FailedPaths` report to surface the scope of the problem to the user.

## Migration Plan

No migration needed - purely additive fault-isolation to existing code paths. No
schema, config, or manifest format changes. Deploys as a normal code change; no
rollback considerations beyond reverting the commit.
