## Context

`ISnapshotRepository.GetCurrentState()` (implemented by `SqliteSnapshotRepository`) returns an
`IReadOnlyDictionary<string, CurrentFileState>` keyed by relative path, built fresh on each call
from the `file_versions` table. `BackupDiffer.Diff` looks up each scanned source path in that
dictionary to decide whether it is unchanged, changed, or a new addition, and separately computes
the deleted set by checking which recorded paths are absent from an `OrdinalIgnoreCase` set of
scanned paths. See proposal.md - Why for the resulting failure mode when the dictionary's default
comparer is ordinal/case-sensitive while the deleted-set check is case-insensitive.

## Goals / Non-Goals

**Goals:**
- Make `GetCurrentState()`'s path lookup case-insensitive, consistent with every other
  path-keyed collection in the codebase.
- Ensure a casing-only rename is matched to its existing manifest entry instead of being
  misclassified as an addition, and that the prior entry is never left orphaned as a result.

**Non-Goals:**
- Renaming the mirror/manifest entry to reflect the new casing when only casing changed. Size
  and modification time still govern "unchanged" classification (per the existing Incremental
  change detection requirement); a casing-only rename with matching size/mtime is treated as a
  no-op, not as a move requiring a mirror-side rename. Actively re-casing the mirror path is out
  of scope for this fix.
- Changing the on-disk manifest schema, SQL query shape, or `CurrentFileState`/`FileVersionRecord`
  contracts.
- Addressing move/rename detection generally (already covered by the existing "Move and rename
  detection" requirement) - this fix only closes the case-sensitivity gap in the *lookup*, not
  the move-detection algorithm itself.

## Decisions

- **Construct the dictionary with `StringComparer.OrdinalIgnoreCase`.** This mirrors the exact
  comparer already used by `BackupDiffer`'s `scannedPaths`, `BackupPlanner`'s
  `consumedAsMoveSource`/results dictionary, `YamlProfileConfigLoader`'s `seenNames`, and
  `SqliteSnapshotRepository.GetFileHistory`'s own `visited` set - so the fix is a one-line,
  minimal-risk change with an established, already-reviewed precedent in the same codebase
  rather than a new pattern.
  - Alternative considered: normalize casing at write time (e.g., lower-case all
    `relative_path` values before storing). Rejected - it would lose the original casing
    entirely (harming restore/history commands that display the tracked path to the user) and
    would require a data migration for existing manifests, whereas an in-memory comparer change
    requires neither.
  - Alternative considered: fix only `BackupDiffer`'s lookup call site instead of the
    dictionary's comparer. Rejected - `GetCurrentState()`'s result is a general-purpose port
    method (`ISnapshotRepository`) that other callers can consume directly; fixing it at the
    source keeps the guarantee co-located with the data it protects instead of relying on every
    caller to remember to do a case-insensitive lookup themselves.

## Risks / Trade-offs

- [Two source paths differing only by case being treated as the same manifest entry] -> This is
  the intended fix, but note it also matches the existing behavior of every other path-keyed
  collection in the codebase (all already `OrdinalIgnoreCase`), so no new inconsistency is
  introduced; a case-sensitive-filesystem host is out of scope since the codebase does not
  target one today.
- [No mirror-side rename when only casing changes] -> Acceptable: the existing diff logic never
  compared path casing as part of "unchanged" classification (only size/mtime), so this fix
  restores consistency with that pre-existing rule rather than changing it.

## Open Questions

- Should this repo have a cross-layer integration test project (e.g. one that references both
  `Vara.Application` and `Vara.Infrastructure`) so pipeline-level regression tests can run
  against real port implementations instead of hand-maintained fakes? This change's regression
  test (task 2.3) still exercises `FakeSnapshotRepository`, not the real
  `SqliteSnapshotRepository`, because no such project exists today and `Vara.Application.Tests`
  has no reference to `Vara.Infrastructure`. Deferred to a follow-up proposal after this change
  is archived - does not change this change's specs, approach, or task breakdown.
