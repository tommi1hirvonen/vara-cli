## Context

See proposal.md - Why for motivation. Key existing facts this design builds on:

- Mirror paths are derived from absolute source paths by stripping the drive letter's colon
  (`AbsolutePathMirrorMapper.ToMirrorPath`, `src/Vara.Infrastructure/FileSystem/
  AbsolutePathMirrorMapper.cs`), and are otherwise unchanged - this is deterministic and
  reversible for drive-letter paths (UNC sources are already out of scope for this mapper).
- A profile's manifest lives at `<TargetRoot>\.vara\profile.db`
  (`ProfileServiceFactory.CreateFor`, `src/Vara.Cli/Composition/ProfileServiceFactory.cs`); the
  live mirror is `TargetRoot` itself. Both `ISnapshotRepository` and `IContentStore` only need
  `TargetRoot` to be constructed - no other profile field (sources, retention, concurrency) is
  needed to browse or restore history.
- `file_versions` (`src/Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs`) stores one
  row per change, with `relative_path`, `change_kind`, `recorded_at`, `content_hash`, `size`.
  `GetCurrentState()` already computes "latest row per `relative_path`, excluding deleted" via a
  `MAX(id)`-per-path join; this same shape extends directly to "as of a date" (bound the inner
  `MAX(id)` by `recorded_at <= @asOf`) and to "tombstones" (flip the outer filter to `change_kind
  = 'Deleted'`).
- `SnapshotHistoryService` (`src/Vara.Application/History/SnapshotHistoryService.cs`) is the
  sole point where `ISnapshotRepository` and `IContentStore` are composed for browsing/restoring;
  `RestoreCommand`/`HistoryCommand` (`src/Vara.Cli/Commands/`) are thin CLI wrappers over it.
  `IContentStore.ExtractTo` writes a blob to a destination file; there is no member that returns
  a readable `Stream` for a blob today.
- `RestoreCommand` already implements a mirror-containment guard, an overwrite guard
  (`DestinationExistsException`), and an interactive confirmation prompt gated on
  `Console.IsInputRedirected` (see the archived `restore-overwrite-guard` change) - this design
  reuses that machinery rather than replacing it.

## Goals / Non-Goals

**Goals:**
- Resolve all four accepted path forms (literal mirror-relative, cwd-relative-in-mirror,
  cwd-relative-in-source, absolute-source) through one shared, testable resolver, used
  identically by `history`, `restore`, `show`, `diff`, and the directory path argument of
  `browse`/`deleted`.
- Add exactly the new repository/content-store primitives needed (point-in-time state, current
  and point-in-time tombstones, a readable blob stream), keeping every existing member's
  behavior and signature unchanged.
- Keep the upward `.vara\profile.db` search fully independent of `~/.vara/profiles.yml` - a
  relocated or copied mirror is browsable on its own.
- Reuse the existing overwrite-guard/prompt pattern for `--in-place`, and the existing
  change-kind color convention (`HistoryTablePresenter.ChangeKindStyle`) for marking deleted/moved
  entries in the new listings.

**Non-Goals:**
- Auto-detecting a profile from a working directory inside a *source* (as opposed to the mirror).
  Explicitly deferred per proposal scope; source-side input still requires an explicit profile
  name, though its path argument is still resolved flexibly.
- Recursive directory listing, or a tree-wide bulk restore/undelete of a whole directory. `browse`
  stays one level; restoring multiple files still means one `restore` call per file.
- Binary-aware diffing. `diff` shows a textual diff; a binary file's diff is undefined/best-effort
  output, matching how `show`'s raw-bytes-to-stdout behavior for binary content is left to the
  user to recognize (per the user's explicit acceptance of this limitation).
- Any change to manifest schema, storage layout, retention, or backup/prune behavior.

## Decisions

**One `PathResolver` service in `Vara.Application`, not per-command duplication.** A new
`SnapshotPathResolver` (or similar) takes the resolved `TargetRoot`, a set of candidate
mirror-relative paths to test literal membership against (supplied via a lookup delegate into
`ISnapshotRepository`, e.g. "does this path have history?"), the raw user input, and the current
working directory, and returns the resolved mirror-relative path using the priority chain from
the `snapshot-history` delta's "Flexible path input" requirement. `HistoryCommand`, `RestoreCommand`,
the new `ShowCommand`/`DiffCommand`, and `BrowseCommand`/`DeletedCommand`'s directory argument all
call it, so the resolution rule can never drift between commands. Alternative considered: resolve
paths ad hoc inside each command. Rejected - the four-way ambiguity is subtle enough (see the
prior exploration's literal-match-first ordering) that duplicating it risks the commands silently
disagreeing on edge cases.

**`AbsolutePathMirrorMapper` gains `FromMirrorPath` and becomes non-`internal`.** The reverse
transform (re-insert `:` after a single-letter leading segment) is needed by both the path
resolver (to test the "absolute source path" interpretation is well-formed) and by `--in-place`
restore (to compute the original source destination from a mirror-relative path). It stays a pure,
stateless string transform with no filesystem access, consistent with its current design.

**Directory listing and tombstone queries live on `ISnapshotRepository`, not computed in-memory
from `GetFileHistory` per path.** New members: `GetStateAsOf(DateTimeOffset asOf)` (mirrors
`GetCurrentState`'s shape, bounded by date) and `GetTombstones(DateTimeOffset? asOf)` (latest row
per path where that row's `change_kind = Deleted`, optionally bounded by date). Both return the
same flat, whole-profile dictionary/list shape as `GetCurrentState`; `SnapshotHistoryService`
does the directory-prefix filtering and one-level synthesis (grouping remaining path segments
into immediate file vs. subdirectory entries) in memory, the same way `GetCurrentState`'s
existing consumers already filter its output. Alternative considered: push prefix filtering into
SQL via a `LIKE`/`GLOB` clause per call. Rejected for the initial implementation - profiles are
personal-scale (thousands, not millions, of tracked paths per the existing `GetCurrentState`
precedent, which already loads the whole profile into memory), so an in-memory filter keeps the
SQL surface small and reuses one query shape for both `browse` and `deleted`.

**Moved-out detection is a query-side join, not a scan.** To mark an entry "moved" instead of
"deleted" in a directory listing (per the "Entry moved out of the listed directory" scenario),
`GetTombstones` includes any row whose *latest* record is `Deleted` even if an earlier record in
that path's history was itself the target of a later `Moved` record elsewhere - concretely: a
path's own latest row already says whether it was deleted (`change_kind = Deleted`) or now lives
elsewhere. "Moved out" is detected by checking whether any *other* currently-live path's most
recent `Moved` record has this path as its `previous_relative_path` - a lookup against
`GetCurrentState()`'s rows filtered to `change_kind`-agnostic moved-from links. This requires
`FileVersionRecord`'s existing `PreviousRelativePath` to be queryable in bulk (a new
`GetMoveOrigins()`-shaped read, or folding it into `GetTombstones`'s result), decided at
implementation time in tasks.md once the exact SQL shape is prototyped - the requirement is
observable behavior (mark it "moved", not "deleted"), not a mandated query structure.

**`IContentStore` gains `OpenRead(string hash)` returning a `Stream`.** `show` writes that stream
to `Console.OpenStandardOutput()`; `diff` reads both versions' streams into memory (version
content is already known to be reasonably sized for hardlink/copy operations elsewhere in this
codebase) and hands them to a text-diff routine. Alternative considered: add a `Show`/`Diff`-
specific method directly on `IContentStore`. Rejected - `OpenRead` is the more primitive, reusable
capability; text-diffing is an `Vara.Application`-layer concern, not a storage concern.

**`--in-place` reuses `SnapshotHistoryService.RestoreAsOf`/`RestoreVersion` unchanged, with
`RestoreCommand` computing the destination.** `RestoreCommand` computes the in-place destination
via `AbsolutePathMirrorMapper.FromMirrorPath(resolvedPath)` before calling the same restore method
it already calls for `--out`, so the existing mirror-containment/overwrite-guard/progress-
reporting code paths apply unchanged. `--out` and `--in-place` are validated as mutually exclusive
in `RestoreCommand`'s option parsing, before any resolution happens.

**Interactive version picker reuses `HistoryTablePresenter`'s row rendering via a
`SelectionPrompt<FileVersionRecord>`.** `RestoreCommand` already branches on
`OutputMode.IsLiveCapable(console)` for its progress bar; the same capability check gates whether
an omitted `--version`/`--at` triggers the picker or the existing hard error. The prompt's choice
labels reuse the same formatting `HistoryTablePresenter` uses per row (id, recorded-at, change
kind, size) so the two views stay visually consistent.

**`browse`/`deleted` are new commands, not new flags on `history`.** `history` is about one
path's version timeline; `browse`/`deleted` are about a directory's or profile's current/point-
in-time *shape*. Keeping them separate commands matches the existing one-command-per-shape
convention (`snapshots` vs `history` vs `restore`) rather than overloading `history`'s argument
semantics.

## Risks / Trade-offs

- [In-memory prefix filtering over `GetStateAsOf`/`GetTombstones`'s whole-profile result set could
  become slow for a profile with very many tracked paths] → Accepted for now, consistent with
  `GetCurrentState`'s existing whole-profile-in-memory precedent; revisit with a SQL-side prefix
  filter if this proves slow in practice.
- [Moved-out detection adds a second read (or an extra join) beyond a plain tombstone query] →
  Mitigated by resolving the exact query shape in tasks.md against the real schema rather than
  locking it in here; the observable contract (mark as "moved") is what the spec requires.
- [Four-way path ambiguity resolves silently to whichever interpretation matches first, which
  could mask a typo that happens to coincidentally match a different tracked path] → Accepted;
  the literal-match-first rule preserves today's exact behavior for existing callers, and a
  coincidental cross-match is an edge case no worse than today's single-interpretation behavior.
- [`--in-place` writes over a real working file outside the mirror, a new destination class this
  codebase has not written to before] → Mitigated by reusing the existing, already-reviewed
  overwrite-guard/prompt code path verbatim rather than introducing new destination-safety logic.

## Migration Plan

No data migration - purely additive commands/flags plus new read-only queries. `--in-place` is
opt-in (a new flag), so no existing `restore --out` invocation changes behavior. The path-input
change is additive per the literal-match-first rule, so no existing script that passes a literal
mirror-relative path changes behavior.
