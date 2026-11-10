## Context

See proposal.md - Why. What an implementer in a fresh session needs:

- `RestoreRun.writeFile` (`src/Vara.Engine/Runs/RestoreRun.fs`) opens the blob through `env.OpenBlob`, copies it through a `TeeStream` to a staging file beside the destination (`tee.CopyTo Stream.Null`, so the tee does the writing and reports bytes), flushes it, sets the modification time, clears the destination's read-only attribute and moves the staging file over the destination. Any failure removes the staging file (`with _ -> tryDelete staging; reraise ()`).
- `RestoreWrite.Source` is the `ContentHash` of the version's content (`src/Vara.Domain/Snapshots/Restore.fs`), so the expected hash is already in the plan.
- Per-file failures are `IOException` or `UnauthorizedAccessException` caught by `attempt`; the failure's reason is the exception message. A single-file restore then ends through `HistorySession.fail` (hard error, exit 1); a directory restore lists the failures and ends as a partial failure (exit 2). Both paths already exist and are what the spec asks for.
- `Hashing.hashStreamReporting` (`src/Vara.Engine/Foundation/Hashing.fs`) hashes a stream in 80 KiB reads and takes a per-chunk callback. `Hashing.hashStream` reads in the hash library's smaller chunks.
- `MirrorPathEscapesTargetRootException` in `ContentStore.fs` is the precedent for a small `IOException` subtype used so that an existing per-operation failure path handles a new case.
- Tests: a module named after the requirement title, a test named after the scenario title (F# double-backtick names). `tests/Vara.Testing/SpecCoverage.fs` fails when a main-spec scenario has no test, which applies once this change is synced. The existing restore tests in `tests/Vara.Engine.Tests/Runs/HistoryEngineTests.fs` replace `OpenBlob` with a function returning a different stream; do the same here, returning a stream with other bytes.

## Goals / Non-Goals

**Goals:**
- A restore never replaces a destination with bytes that are not the recorded content.
- Verification uses the pass that already reads the bytes; no second read of the blob.
- The mismatch flows through the existing per-file failure handling.

**Non-Goals:**
- `show` writes a blob to standard output as it reads it, so a mismatch could only be reported after the bytes were already emitted; `diff` is bounded to small files and compares them to each other. Neither is changed here. Whether they should be is a separate decision.
- Repairing a damaged blob, or searching for another copy of the content (the live mirror may hold one). `vara check` reports damage.
- Verifying links (they have no content) or the destination after the move.

## Decisions

**Hash while copying, compare before the flush and the move.** Replace `tee.CopyTo Stream.Null` with `Hashing.hashStreamReporting ignore tee`. The tee still writes every byte to the staging file and reports progress per read; the hash consumes the same bytes. Compare the result with `write.Source` immediately, before `sink.Flush()` and `target.Flush true`, so a damaged file wastes no flush. On a mismatch raise the exception below; the existing handler removes the staging file and the destination is never touched because the move comes later.
Alternatives considered: a second pass over the staged file (an extra full read); hashing in `TeeStream` itself (couples a generic stream decorator to this one use); `Hashing.hashStream` (works, but its smaller reads would make the tee report about twenty times more progress events, and each event takes the progress lock in the CLI).

**A dedicated `IOException` subtype for the mismatch.** `RestoredContentMismatchException(expected, actual)` with a message that says the stored content does not match what was recorded for the file and suggests running `vara check` to find damaged content. Being an `IOException` it takes the existing per-file path, so no new failure plumbing is needed in `attempt`, `applyAll`, the single-file command or the directory command. Alternative: a new case in `RestoreFailure` (a domain type change that every consumer of the restore result would have to handle).

**Message wording.** The spec fixes only that the reason states that the stored content does not match what was recorded. Keep the hashes in the message (short form) so a user can compare with `vara check`'s report.

## Risks / Trade-offs

- [Hash cost on every restored byte] → XxHash128 is far faster than the disk; confirm with the existing restore tests and a manual restore of a large file, no benchmark needed.
- [A recorded hash that is wrong for a reason other than a damaged blob (for example a manifest written by an older build)] → The manifest hash is the content hash computed by the same function at backup time; if a mismatch appears for healthy data, that is a defect worth surfacing, not hiding.
- [A restore of a damaged file used to "work"] → That is the point of the change; the failure names the cause and the next step.
