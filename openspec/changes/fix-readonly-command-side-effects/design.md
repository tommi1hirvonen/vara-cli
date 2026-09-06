## Context

`ProfileServiceFactory.CreateFor` is the single composition point every command uses to obtain a
profile's `ISnapshotRepository`/`IContentStore`, including read-only commands (`snapshots`,
`browse`, and by extension `history`/`show`/`diff`/`deleted`, which resolve the same way). Both
concrete implementations create filesystem state in their constructors - `SqliteSnapshotRepository`
creates its containing directory and opens (thus creates) `profile.db`; `FileSystemContentStore`
creates the mirror/versions/temp directories. `backup` legitimately needs this eager creation (it's
about to write). See proposal.md for why this is a problem for read-only commands.

## Goals / Non-Goals

**Goals:**
- Read-only commands stop creating `.vara\` state for a profile that has never been backed up.
- `backup` (and any other command that writes) keeps today's eager-creation behavior unchanged.

**Non-Goals:**
- Changing what `backup` does on first run for a brand-new profile - unaffected.
- A general-purpose "read-only vs. read-write" service architecture - this fix only needs to answer
  the question for the current, small set of commands.

## Decisions

- **Distinguish "open if exists" from "open or create" at the factory level**, e.g.
  `ProfileServiceFactory.CreateFor(profile, createIfMissing: false)` for read-only commands vs. the
  existing eager-creation behavior (default `true`, preserving today's call sites) for `backup`.
  - Alternative considered: make the constructors themselves lazy (defer directory/file creation
    until first write). Rejected as a larger, riskier change touching write-path behavior for every
    caller including `backup`, for a problem that's specific to a handful of read-only commands.
  - Alternative considered: probe for the profile's existence in each read-only command before
    calling the factory, leaving the factory/constructors unchanged. Rejected as duplicated,
    easy-to-forget logic repeated per command; the factory is the single choke point today and
    should stay that way.
- **`SqliteSnapshotRepository` gets a "does this profile have any recorded state" existence check**
  (e.g. checking whether `profile.db` exists before opening) that read-only commands call before
  ever constructing the repository, so "nothing recorded yet" can be reported without opening a
  connection that would otherwise create the file as a SQLite side effect.
- **`FileSystemContentStore`'s directory creation is guarded behind the same `createIfMissing`
  flag**, since it's constructed by the same factory call for the same profile.

## Risks / Trade-offs

- [Every current call site of `ProfileServiceFactory.CreateFor` must be triaged read-only vs.
  read-write to pick the right flag; missing one silently reintroduces the bug for that command] →
  Mitigated by an explicit task enumerating every call site (already found: `Program.cs`,
  `BackupCommand`, `DeletedCommand`, `BrowseCommand`, `HistoryCommand`, `RestoreCommand`,
  `PruneCommand`, `SnapshotsCommand`, `DiffCommand`, `ShowCommand`) and a test asserting no `.vara\`
  directory results from running each read-only command against a fresh profile.
- [`PruneCommand` writes (deletes expired snapshots/blobs) but might be miscategorized as read-only
  since its name doesn't suggest creation] → Treated as read-write (`createIfMissing: true`,
  unchanged) since pruning requires existing state to operate on in the first place; verified during
  the call-site triage task above.

## Open Questions

(none)
