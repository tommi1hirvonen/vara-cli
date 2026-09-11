## Context

See `proposal.md` for motivation. Single-file `restore` (no `--recursive`) resolves its `path`
argument via `SnapshotPathResolver.TryResolve` with a `hasHistory` predicate of
`services.Repository.GetFileHistory(candidate).Count > 0`. A directory never has file-version
rows, so every candidate the resolver tries for a directory input fails, the path is left as the
raw, unresolved input, and the subsequent lookup (`history.GetFileHistory(path)`, used either to
show the interactive version picker or as `RestoreAsOf`/`RestoreVersion`'s target) finds nothing
and raises `NoHistoryForPathException`. That path is indistinguishable, from the user's side, from
a path that was genuinely never tracked at all.

`RestoreCommand` already has everything needed to tell the two cases apart: `IsDirectoryTracked`
(a private helper built on `SnapshotHistoryService.ListDirectory`) is used today by
`RunRecursiveRestore` to check whether a directory argument is tracked.

## Goals / Non-Goals

**Goals:**
- When single-file `restore`'s path resolution fails to find a tracked file, check whether the
  unresolved input is nonetheless a tracked directory, and if so, fail with a distinct, actionable
  error instead of `NoHistoryForPathException`.
- Keep the check cheap and side-effect-free: only runs after file resolution has already failed,
  so the common case (a valid file path) pays no extra cost.

**Non-Goals:**
- No change to `SnapshotPathResolver.TryResolve` itself, or to its resolution order/semantics for
  file paths - this only adds a fallback check after resolution already failed.
- No change to `restore --recursive`'s own resolution, messaging, or `IsDirectoryTracked` helper
  beyond making it accessible to the single-file branch.
- No attempt to auto-detect intent and silently run a recursive restore instead - the user must
  explicitly re-run with `--recursive`, consistent with `--recursive`'s existing mutual-exclusion
  with `--version` and its own distinct confirmation/progress behavior.

## Decisions

**Decision: Reuse the existing `IsDirectoryTracked(history, candidate)` helper, called on the raw
(pre-resolution) input, only after the single-file resolver fails to match a tracked file.**
`IsDirectoryTracked` already wraps `SnapshotHistoryService.ListDirectory` with the
tracked/not-tracked `NoSuchDirectoryException` check `RunRecursiveRestore` relies on, so no new
history-querying logic is needed. It is currently `private`; change it to `internal` (or hoist to
a shared location) so the single-file branch in the same class can call it. Running it only after
file resolution fails - not as a first check - keeps the common, successful case (an actual
tracked file) exactly as fast as today.

**Decision: New exception type `RestoreTargetIsDirectoryException`, not a reused/extended
`NoHistoryForPathException`.** The two errors mean different things ("nothing is tracked here" vs.
"something is tracked here, but as a directory") and callers/tests should be able to tell them
apart programmatically, mirroring how `NoSuchDirectoryException` is already kept distinct from
`NoHistoryForPathException` for the directory-listing case. Message: something like `"'<path>' is
a directory. Pass --recursive to restore it as a directory."` - actionable, names the exact flag
needed.

**Decision: The new check only applies to the single-file `restore` branch, not to `history`,
`show`, or `diff`.** Those commands have no directory-mode equivalent, so telling a user "pass
--recursive" would be actively wrong for them. Restore's proposal explicitly scopes this to
`restore` alone.

## Risks / Trade-offs

- [Risk] Calling `IsDirectoryTracked` after every failed file resolution adds one extra
  `ListDirectory` query (a `GetCurrentState()`/tombstone scan) to what is already an error path →
  Mitigation: only reached when the file lookup already failed, i.e. the command is about to error
  out regardless; the extra query cost is irrelevant next to reporting an error and exiting.
- [Trade-off] `IsDirectoryTracked`'s visibility moves from `private` to `internal`, slightly
  widening `RestoreCommand`'s internal surface → accepted since it is already unit-tested
  indirectly through `RunRecursiveRestore` and both call sites live in the same file.
