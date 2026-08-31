## Context

See proposal.md - Why. Today `BackupPlanner.Plan()` (`src\Vara.Application\Backup\BackupPlanner.cs`) resolves move candidates with a synchronous `foreach` over `diff.Pending`: for each `Added` entry sharing a size with a `Deleted` candidate, `TryHashFile` opens the source file and calls `IHasher.ComputeHash` on the *entire* stream, once per entry, before any candidate comparison happens. This full read happens whether or not the entry turns out to be a real move, and happens entirely before `BackupExecutor.Execute` starts its own parallel `Add`/`Change` transfer loop, which performs its own full read of the same source file for genuine adds/changes.

The manifest (`ISnapshotRepository`, backed by `SqliteSnapshotRepository`) records one `FileVersionRecord` per change and exposes the latest state of every tracked path via `GetCurrentState()` returning `CurrentFileState`. Both types currently carry only a full `ContentHash` (`src\Vara.Core\Snapshots\FileVersionRecord.cs`). A deleted candidate's file no longer exists on disk by definition, so nothing short of a previously-recorded signature can support a cheap pre-filter against it - there is no way to re-derive a bounded-prefix signature for a candidate after the fact.

This project is unreleased with no data in the field, so schema changes carry no migration/backfill burden.

## Goals / Non-Goals

**Goals:**
- Remove the serial bottleneck: move-detection hashing runs concurrently across size-matched candidates.
- Remove wasted full-file reads for candidates that are not real moves, by ruling most of them out from a small bounded read instead of the whole file.
- Keep move-detection's final answer exactly as accurate as today - a match is only ever confirmed by an exact full-content hash comparison; the bounded-read signature can only rule a candidate *out*, never confirm one *in*.
- Make the new manifest field's scheme resilient to future changes (different window size or algorithm) without requiring a migration or backfill, ever.

**Non-Goals:**
- Not changing `IHasher`'s existing single-call `ComputeHash(Stream)` contract - it remains the primitive used for confirming a full-content match. The new streaming/early-abort logic is a separate helper, not a change to `IHasher`.
- Not eliminating the transfer stage's own read of newly-added/changed files - that read performs the actual copy into the content store and is unavoidable regardless of move detection.
- Not addressing `FileSystemContentStore.StoreFromStream`'s separate, already-documented write-then-read-back hashing simplification - that is pre-existing, unrelated internal detail (see its own code comment) and out of scope here.
- Not attempting any backward-compatible migration of existing manifests - not needed pre-release, and the scheme-tagging design (below) means no migration will ever be needed even after release.

## Decisions

**Two-phase planning: parallel hash computation, then serial matching.**
Split `BackupPlanner`'s current single `foreach` into (1) a `Parallel.ForEach` (mirroring `BackupExecutor.Execute`'s existing pattern) that computes each size-matched `Added` entry's signature into a `ConcurrentDictionary<string, ...>` keyed by relative path, then (2) the existing serial matching loop, unchanged in its logic and iteration order, just reading precomputed signatures from the dictionary instead of hashing inline. This keeps `consumedAsMoveSource` and candidate-consumption order exactly as deterministic as today (first non-consumed candidate in `diff.DeletedPaths` order wins ties) - only the expensive I/O/hash work moves off the critical serial path, not the decision logic itself. Alternative considered and rejected: parallelizing the whole match loop (including consumption) - rejected because `consumedAsMoveSource` is mutated per-match and making that safe under concurrency (locking, or reordering matches) risks changing which candidate wins a tie, an observable behavior change this proposal explicitly excludes.

**Single continuous streaming read per candidate, with early abort - not a separate prefix read followed by a full read.**
`TryHashFile`'s replacement reads the source file once, in a loop: accumulate the first *W* bytes into a "quick" hash while also feeding every byte into a full hash. Once *W* bytes have been read, compare the quick hash against every still-viable same-size deleted candidate's recorded quick hash (from `CurrentFileState`). If none match, close the stream immediately - no further bytes are read. If at least one matches, continue reading to EOF (continuing to feed the same full hash - no re-open, no re-read from the start) and use the resulting exact full hash to confirm (or rule out) the match against the full `ContentHash` of the candidates whose quick hash matched. Alternative considered and rejected: read a bounded prefix, decide, then separately call the existing single-shot `IHasher.ComputeHash` on a fresh full read for confirmation - rejected because it reads the first *W* bytes twice (once for the prefix, once again as part of the full read), which is exactly the kind of redundant read this change is meant to eliminate.

**Quick hash is recorded going forward, on bytes already being read - not computed retroactively.**
`CurrentFileState` and `FileVersionRecord` gain a nullable `QuickHash` (plus `QuickHashScheme`, see below). It is populated whenever a file's content is newly recorded: `FileSystemContentStore.StoreFromStream`/`BackupExecutor`'s transfer path compute it from the same bytes already being streamed into the content store (negligible marginal CPU, no marginal I/O), and a confirmed Move operation carries the matched candidate's existing `QuickHash` forward to the new path (content, and therefore its quick hash, is provably identical - no re-hash needed). Alternative considered and rejected: a background job that retroactively computes quick hashes for existing manifest rows - unnecessary complexity for a pre-release project with no existing data to backfill.

**Scheme-tagged quick hash, not a bare hash value.**
Every recorded `QuickHash` is paired with an integer `QuickHashScheme` identifying the window size/algorithm used to produce it. Move detection only trusts a candidate's `QuickHash` for pre-filtering when its `QuickHashScheme` matches the scheme the current code computes; a mismatch (including "no quick hash recorded at all", encoded as `QuickHashScheme` absent/zero) is treated identically to "no signature available" - the candidate simply falls back to a full-content read and comparison, exactly like today's behavior for that one candidate. This is what makes the design future-proof without a migration: bumping the scheme constant when the window size or algorithm changes is enough - old rows age out of the fast path naturally as their files are eventually re-added/changed and re-recorded under the new scheme, and nothing ever needs to be rewritten in bulk.

**Bounded window size *W* is a tunable constant, not a protocol decision.**
A fixed prefix length (candidate default: 64 KB) is read for the quick hash. The exact value trades off pre-filter precision (larger *W* rules out more false candidates before needing a full read) against per-file overhead for files smaller than or close to *W* (where the "quick" read is nearly the whole file anyway, so there's little to save). This is safely deferrable - see Open Questions.

## Risks / Trade-offs

- [Risk] A pathological input could have many same-size candidates whose *quick* hashes all collide by coincidence, degrading to today's full-read behavior for all of them → Mitigation: this only ever costs as much as today's behavior (never worse), since a quick-hash match simply triggers the same full read/compare that happens unconditionally today; the pre-filter can only reduce work, never add it beyond one small bounded read per candidate.
- [Risk] Concurrent hashing increases peak concurrent file-handle/disk-queue usage during planning, on top of whatever the subsequent transfer stage will also use → Mitigation: reuse the same `MaxDegreeOfParallelism` sizing convention `BackupExecutor` already uses (`Environment.ProcessorCount` by default), so planning's concurrency profile matches a pattern already proven acceptable for this codebase's transfer stage.
- [Trade-off] Adding `QuickHash`/`QuickHashScheme` to the manifest schema is a small durable storage/complexity increase (two more nullable columns, plus the scheme-matching logic) in exchange for avoiding full-file reads for non-matching candidates at scale → accepted, since the proposal's estimated win (potentially orders of magnitude fewer bytes read in the large-reorganization scenario this feature targets) outweighs the modest schema/logic addition.
- [Risk] `RecordFileVersion`'s signature grows (new parameters) → Mitigation: this project has no external/plugin consumers of `ISnapshotRepository` to break; all call sites are internal and updated as part of this change (see tasks.md).

## Migration Plan

None required. The project is unreleased, so the new nullable `QuickHash`/`QuickHashScheme` columns are added directly to the manifest schema with no existing rows to backfill. Should a manifest exist from pre-change testing, rows written before this change simply have no recorded quick hash, which move detection already treats as "fall back to full read" (see the "Recorded signature unavailable for a candidate" scenario in this change's spec delta) - no explicit backfill step is needed even for those.

## Open Questions

- Exact bounded window size *W* for the quick hash (candidate default: 64 KB) - tunable without changing the approach, the spec, or the task breakdown; can be adjusted based on benchmark results during implementation.
