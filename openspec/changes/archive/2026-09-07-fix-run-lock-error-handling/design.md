## Context

See proposal.md - Why. `FileRunLock.TryAcquire(profileName, targetRoot)` (`src/Vara.Infrastructure/Concurrency/FileRunLock.cs`) opens an exclusive `FileStream` on `<targetRoot>\.vara\run.lock`, ignoring `profileName` entirely - correct, since the lock's actual invariant is "one run per target," not "one run per profile." Both `BackupPipeline` and `PruneService` call it and, on `false`, throw `BackupAlreadyRunningException`/`PruneAlreadyRunningException` respectively, each naming only the calling profile. `TryAcquire` only catches `IOException` around the `FileStream` open; `UnauthorizedAccessException` (thrown when the process lacks permission on the directory or file) is not an `IOException` subclass in .NET and currently escapes unhandled.

## Goals / Non-Goals

**Goals:**
- Make the reported message accurate regardless of which profile(s) share a target, without needing to know or report the actual current holder.
- Make lock-acquisition permission failures produce a clear, distinct error instead of an unhandled exception or a misleading "already running" message.
- Keep the fix symmetric across both call sites (`BackupPipeline` and `PruneService`), since both share the exact same lock and failure modes.

**Non-Goals:**
- Not implementing holder identification (writing PID/profile metadata into the lock file so a message can name the actual current holder). That was considered and explicitly deferred - it requires the lock file to carry content and a read-back path, which is a materially bigger change than correcting the message's wording. This change only makes the existing message stop asserting something it can't know.
- Not changing `FileRunLock`'s scope: it remains correctly per-target, not per-profile.

## Decisions

**Reword rather than identify the holder.** The message changes from "A backup run for profile 'X' is already in progress" to language describing the *target* as busy (e.g. "Another operation is already using target '<targetRoot>'; a backup/prune run for profile 'X' cannot start until it finishes"), which is accurate whether the actual holder is the same profile or a different one sharing the target. Rejected alternative: writing the holding profile's name/PID into the lock file's content so the blocked run could report exactly who holds it - correct in principle, but a bigger change (needs a write-then-read protocol for the lock file's payload, and handling a stale or partially-written payload) for a problem the reworded message already resolves adequately.

**Add a distinct exception for lock-acquisition access-denied, rather than returning `false`.** `IRunLock.TryAcquire`'s existing doc comment only promises "no throw" for the *already-held* case ("Returns `false` (without throwing) if another run already holds it") - it does not promise `TryAcquire` never throws for an unrelated failure. Returning `false` for an `UnauthorizedAccessException` would route the caller into the same "already in progress" message, which is just as misleading as the profile-naming bug this change fixes (it would blame contention for what is actually a permissions problem). Instead, `FileRunLock.TryAcquire` catches `UnauthorizedAccessException` and throws a new `RunLockAccessDeniedException(targetRoot)` (placed in `src/Vara.Core/Concurrency/RunLockExceptions.cs`, alongside `IRunLock`'s existing `Vara.Core.Abstractions` home but in its own file, following the existing per-domain `*Exceptions.cs` convention used by `BackupExceptions.cs`/`PruneExceptions.cs`/`ProfileConfigExceptions.cs`), which both `BackupPipeline` and `PruneService` let propagate as-is (both already let `IRunLock.TryAcquire`'s other outcomes surface through unhandled exception types elsewhere, e.g. `IOException` from later stages, so this is consistent with existing call-site behavior).

**Keep both call sites symmetric.** `BackupAlreadyRunningException` and `PruneAlreadyRunningException` get the same message wording change; `RunLockAccessDeniedException` is thrown from the one shared `FileRunLock.TryAcquire` implementation, so both commands get the fix automatically without needing command-specific handling.

## Risks / Trade-offs

- [The reworded message no longer reads as cleanly as "a run for profile X is in progress" for the common single-profile-per-target case] → Word it to cover both cases without being awkward for the common one (e.g. lead with the target, then name the requesting profile), and cover both the shared-target and single-profile scenarios in the delta specs' scenarios so the wording is checked against both.
- [`RunLockAccessDeniedException` is a new public exception type that CLI-layer error handling must map to a friendly message, same as existing `BackupAlreadyRunningException`/`PruneAlreadyRunningException`] → Follow the exact existing pattern used for those two exception types at the CLI's error-handling boundary; no new error-handling mechanism is introduced.
