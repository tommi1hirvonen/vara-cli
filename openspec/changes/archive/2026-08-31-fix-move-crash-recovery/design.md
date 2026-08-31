## Context

See proposal.md - Why. `FileSystemContentStore.MoveMirrorEntry` performs an unconditional `File.Move(fromPath, toPath, overwrite: true)` with no check for whether `fromPath` still exists. `BackupExecutor.ExecuteMoveOrDelete` calls it, then issues two `RecordFileVersion` calls (`Deleted` at the old path, `Moved` at the new path) inside the snapshot's batched transaction (`BackupPipeline.BeginManifestBatch`/`Commit`). `PlaceAtMirrorPath` (Add/Change) and `RemoveFromMirror` (Delete) are already idempotent against a retried operation - the former overwrites atomically regardless of prior state, the latter no-ops if the target is already gone. `MoveMirrorEntry` is the only one of the three that assumes its own prior invocation never happened, so a retry after an interrupted commit throws `FileNotFoundException`, which is caught by the existing per-operation try/catch (`isolate-move-delete-failures`) and recorded as a failed path - permanently, since the manifest never advances past the stale pre-crash state that keeps re-selecting the same move.

## Goals / Non-Goals

**Goals:**
- Make a retried move operation idempotent: if the mirror already reflects the move (old path absent, new path already holding the expected content), treat the relocation step as satisfied and proceed to record the manifest rows for it.
- Preserve existing behavior for genuine failures (e.g., new path unwritable, old path locked mid-move) - those must still be caught and reported as failed paths, not silently swallowed.
- Keep the fix local to the move path; Add/Change/Delete recovery already works and is untouched.

**Non-Goals:**
- Not changing the manifest schema, `RecordFileVersion`'s signature, or the batched-transaction design from `batch-manifest-writes`.
- Not attempting to detect or recover interrupted moves for content that changed between the crash and the retry (that's just a normal Add/Change/Delete, already handled).
- Not adding a new content-hash verification pass on every move for the common (non-crash) case - the idempotency check must be cheap and only matter on the recovery path.

## Decisions

**Detect "already relocated" by existence, not content re-hash.**
When `MoveMirrorEntry`'s `fromPath` does not exist, check whether `toPath` already exists. If it does, treat the move as already applied and return normally instead of throwing. Re-hashing `toPath`'s content to confirm it matches the expected hash was considered and rejected: the planner already established the move via a content-hash match between the source file and the deleted candidate's known hash *before* calling into the executor (`BackupPlanner.TryFindMoveMatch`), so by the time `MoveMirrorEntry` runs, a mismatch here would indicate a different, unrelated bug (e.g. another process writing into the mirror), not the crash-recovery scenario this change targets. Existence is the same signal `RemoveFromMirror` already relies on for its own idempotency.

**If neither `fromPath` nor `toPath` exists, that's still a genuine failure.**
This preserves today's behavior for the ordinary case where the mirror entry was never there to begin with (e.g. it was already lost some other way) - `File.Move` throws `FileNotFoundException` as before, which the caller's existing try/catch still converts into a reported failed path. Only the specific "destination already present" case changes.

**Recovery lives in `FileSystemContentStore.MoveMirrorEntry`, not in `BackupExecutor`.**
The idempotency check is a property of the mirror storage operation itself (mirroring how `PlaceAtMirrorPath` and `RemoveFromMirror` are already self-contained), not a concern `BackupExecutor` needs to reason about. This keeps `ExecuteMoveOrDelete`'s call site unchanged and the fix minimal and localized to `FileSystemContentStore`.

## Risks / Trade-offs

- [Risk] If a destination file happens to exist at `toPath` for an unrelated reason (not from a prior interrupted move of *this* content) the recovery path would incorrectly treat a bad move as satisfied and let the manifest record it as moved. → Mitigation: the planner only reaches this call site after already matching the source file's content hash against the deleted candidate's recorded hash, so the only realistic way `toPath` pre-exists is exactly this recovery scenario; a stray unrelated file at that exact relative path would be an existing, separate correctness concern (e.g. mirror tampered with out-of-band) outside this change's scope.
- [Trade-off] Not re-verifying `toPath`'s content hash keeps the recovery path cheap but means a corrupted or truncated destination file from a failed prior `File.Move` (e.g. mid-write crash) would be silently accepted as "already relocated." → Accepted: `File.Move` on the same volume is a single atomic filesystem rename (per `MoveMirrorEntry`'s existing contract), so it cannot itself leave a partially-written destination; this risk would only apply if the underlying `File.Move` implementation changed to a copy+delete, which it does not today.

## Migration Plan

No schema or data migration. This is a behavior fix confined to `FileSystemContentStore.MoveMirrorEntry`; existing manifests and mirrors are unaffected until the next backup run encounters a move to recover, at which point the fixed code path resolves it correctly instead of failing. Rollback is a plain code revert.
