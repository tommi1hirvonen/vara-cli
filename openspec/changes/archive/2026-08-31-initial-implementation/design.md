## Context

This is the first real implementation on top of an existing clean-architecture solution stub (`Vara.Core`, `Vara.Application`, `Vara.Infrastructure`, `Vara.Cli`, target framework `net10.0`, `Vara.Cli` already has `PublishAot` enabled). See `proposal.md` for motivation. The specs in this change (`profile-config`, `backup-execution`, `progress-reporting`, `snapshot-history`, `retention-pruning`) define the observable behavior; this document covers how that behavior is realized technically, targeting Windows only for this change.

## Goals / Non-Goals

**Goals:**
- A concrete data model and pipeline architecture that satisfies all five specs while staying within Native AOT constraints (no unbounded reflection, trimming-safe).
- A storage layout that lets the live mirror be simultaneously a literal, browsable folder tree and a space-efficient, deduplicated, versioned store.
- Clear seams (ports/interfaces in `Vara.Core`) so Windows-specific optimizations (e.g. USN Journal) or VSS integration can be added later without reshaping the pipeline.

**Non-Goals:**
- Cross-platform support (macOS/Linux). Revisit only if the target audience changes.
- VSS/shadow-copy consistency for locked files, USN Journal fast-path scanning, ACL/ADS fidelity, and following symlinks/junctions - all explicitly deferred per the proposal.
- A TUI framework; the CLI renders progress with plain console output via `System.CommandLine` and manual redraw, not a rich TUI library.

## Decisions

### Storage layout
```
<target>\                     the profile's own target root (e.g. D:\backup\ for "files")
  ...                         live mirror - exactly mirrors current source state,
                               placed directly under the target root (real files, or
                               hardlinks into the version store when the target volume
                               supports hardlinks)
  .vara\
    profile.db                SQLite manifest for this profile
    versions\<xx>\<hash>       content-addressed store, one physical file per
                               unique content hash (xx = first two hash characters,
                               for directory fan-out)
    tmp\                      staging area for atomic writes
```
One SQLite database and one version store per profile, colocated directly under that profile's own target root - each profile already has an independent target directory in the sample configuration, so there is no need to nest an extra profile-name segment inside a shared target (an earlier draft of this section nested `<target>\<profile-name>\` and `.vara\<profile-name>\`; that nesting was redundant given every profile already owns its whole target root, and has been simplified away here).

**Alternative considered:** a single shared database/version store across all profiles for cross-profile dedup. Rejected for this change - adds cross-profile reference-counting complexity for a benefit that only applies if two profiles happen to share a target root, which the current configuration model doesn't encourage.

### Hardlink capability probe and degradation
At the start of a run, the system attempts to create and immediately remove a throwaway hardlink inside `.vara\tmp\`. Success or failure is cached for the duration of the run (not persisted, since removable/network drives can change over time) and passed down as a capability flag to the content-store component.

- **Hardlinks supported:** unchanged files are left alone; changed files are hashed while streaming into `tmp`, then hardlinked into `versions\<hash>` (if not already present) and hardlinked again into the mirror path, replacing the old mirror entry.
- **Hardlinks unsupported:** the version store still dedups by hash (skip writing if the hash already exists as a blob), but the mirror path receives an independent copy of the bytes rather than a hardlink.

Move detection is independent of this flag: whenever a hash that existed at path A in the previous snapshot is absent from A but present at a new path B, the mirror file is relocated with a plain rename (works on every filesystem, no hardlink needed).

### Manifest schema (conceptual, not final column-level design)
- `snapshots`: id, started_at, completed_at (nullable), status (`running`/`complete`/`failed`), stats (bytes transferred, files added/changed/moved/deleted, failed file count).
- `file_versions`: profile-relative path, snapshot_id, content_hash, size, source_mtime, event (`added`/`changed`/`moved`/`deleted`/`unchanged`), previous_path (for moves).
- A file's *current* state is the most recent `file_versions` row for its path with an event other than `deleted`. History for a path is every row for that path, most recent first. Deleted files are tombstoned rather than removed from the manifest, so restore-of-deleted still works (`snapshot-history` spec).

Content is referenced by hash only; `file_versions` never stores duplicate content itself, only pointers into `versions\<hash>`.

### Atomicity and crash safety
Every content write follows: stream + hash into `.vara\tmp\<random-name>` -> resolve/create the canonical blob in `versions\<hash>` -> hardlink or copy the blob into the mirror path, replacing any existing entry via rename (never an in-place overwrite). The manifest row for a snapshot is only marked `complete` after every planned file operation succeeds; if the process is killed mid-run, the mirror reflects a valid mix of old/new file states and the snapshot row is left `running`, later reconciled to `failed` by the next run's startup check, which also sweeps orphaned `tmp` entries.

A per-profile run lock (a lock file under `.vara\`, held for the process lifetime) prevents two concurrent runs against the same target.

### Backup pipeline stages
1. **Scan**: walk configured sources (respecting `recursive`, excludes, globs), skipping symlinks/junctions/reparse points (recorded, not traversed).
2. **Diff**: compare each scanned entry's size + modification time against the manifest's current-state view; unmatched/changed entries proceed to content hashing, matched entries are marked unchanged without touching their bytes.
3. **Plan**: build the full set of operations (add/change/move/delete) with byte totals, before any transfer begins - this is what makes upfront progress/ETA possible (`progress-reporting` spec).
4. **Execute**: process the plan with bounded parallelism suited to NVMe queue depth; stream-hash-and-store each changed file per the atomicity model above; apply moves as renames; apply deletions by removing the mirror entry (content stays in the version store, referenced by the tombstoned manifest row).
5. **Commit**: finalize the snapshot row with final stats and mark it `complete`.

### Progress model
The plan step's byte total is the denominator for progress; each execute-stage worker reports bytes written as it streams, giving continuous progress rather than per-file jumps. Move/delete/hardlink operations are counted in the file/operation summary but excluded from the bytes-transferred denominator so they cannot inflate or distort throughput.

### Retention/prune algorithm
Snapshots are bucketed per configured tier (calendar day/week/month/year); within each tier, only the newest snapshot per bucket, up to the tier's configured count, is retained. A snapshot not retained by any tier is eligible for pruning, but pruning operates at the **file-version-row** granularity, not the whole-snapshot granularity: a row is only deleted if it is not the current (latest, non-deleted) row for its path. This matters because a file that hasn't changed in a long time would otherwise have its only manifest row silently deleted once its introducing snapshot ages out of every retention tier, orphaning a still-live file from the current-state view. A snapshot's own record is only removed once none of its rows remain. After row deletion, garbage collection compares the manifest's remaining referenced hashes (a simple SQL query: distinct `content_hash` across all remaining `file_versions` rows, current or tombstoned) against the version store's physical blob listing; any stored blob whose hash is absent from the referenced set is deleted. Splitting it this way (manifest query + store listing + an in-memory set difference in the Application layer) keeps the manifest repository from needing to know anything about physical storage, and the content store from needing to know anything about SQL.

### Composition and libraries
- CLI command surface: `System.CommandLine` for argument/option parsing and command routing (`vara backup <profile>`, `vara prune <profile>`, `vara history <profile> [path]`, `vara snapshots <profile>`, `vara restore <profile> <path> --at <date|version> --out <path>`).
- Dependency injection: `Microsoft.Extensions.Hosting`'s `HostBuilder` as the composition root in `Vara.Cli`, registering `Vara.Infrastructure` implementations against `Vara.Core` ports.
- SQLite access: `Microsoft.Data.Sqlite` (thin ADO.NET wrapper over native SQLite, no heavy reflection-based ORM, AOT-friendly). **Alternative considered:** EF Core - rejected for this data model. The schema is small and stable (two tables), the key queries (latest-row-per-path, calendar-bucketed retention, reference-count garbage collection) are naturally set-based/raw-SQL rather than object-graph-shaped, bulk-inserting large per-snapshot row counts is simpler without change-tracking overhead, and avoiding EF Core's heavier reflection surface reduces Native AOT/trimming risk. Dapper (or an AOT-oriented variant) remains a lightweight fallback if hand-written row mapping becomes tedious.
- Testing: one xUnit test project per `src/` layer (`tests/Vara.Core.Tests`, `tests/Vara.Application.Tests`, `tests/Vara.Infrastructure.Tests`), mirroring `src/`, so `Vara.Core`'s tests never depend on Infrastructure/SQLite and each layer's ports/seams stay independently testable.
- Hashing: a fast non-cryptographic hash (e.g. `System.IO.Hashing`'s XxHash3/XxHash128) computed while streaming file content, since collision-resistance requirements here are about accidental duplication detection, not adversarial security.
- Hardlink creation: `System.IO.File.CreateHardLink` is not available on the `net10.0` target framework (it ships starting with .NET 11) - implemented instead via a small `LibraryImport`-based P/Invoke wrapper around Win32 `CreateHardLinkW`. `LibraryImport`'s source-generated marshalling keeps this fully Native AOT / trimming safe, unlike classic `DllImport` reflection-driven stub generation.
- YAML parsing: needs an AOT/trimming-safe path. If the chosen library's reflection-based deserializer is not fully AOT-safe, use its source-generated/static-context mode, or fall back to a small hand-written mapping layer for the known profile schema. This is called out as an implementation-time verification step in tasks, not resolved here, since it doesn't change any spec-level behavior.

## Risks / Trade-offs

- [Risk] Hardlink capability can change over time for the same target (e.g. a drive reformatted from NTFS to exFAT) → Mitigation: probe at the start of every run rather than trusting a cached/stored value from a previous run.
- [Risk] SQLite manifest file corruption (e.g. from an abrupt power loss during a DB write) could jeopardize the whole profile's history → Mitigation: rely on SQLite's own WAL/journal durability; keep the manifest write for a given file's completion as small and infrequent as practical (batch per snapshot where safe).
- [Risk] Content-addressed store with hash-only identity has a theoretical hash-collision risk (two different contents, same hash) → Mitigation: use a hash wide enough (e.g. 128-bit+) that accidental collision is not a practical concern for this use case; this is a change-detection/dedup aid, not a cryptographic integrity guarantee.
- [Risk] Bounded parallel execution tuned wrong could either under-use NVMe queue depth or thrash a slower disk → Mitigation: keep the concurrency limit configurable/overridable rather than hardcoded, so it can be tuned without a design change.
- [Trade-off] Tiered retention (vs. a flat cutoff) is more complex to implement and reason about, but was an explicit, informed choice to let smaller target disks retain a longer, coarser history.

## Migration Plan

Greenfield change - no existing users or data to migrate. `Program.cs`'s current `Hello, World!` stub is replaced by the `System.CommandLine`/`HostBuilder` composition root as part of implementation.

## Open Questions

- Exact YAML library choice and its AOT-safe usage pattern - to be confirmed during implementation (task-level spike), does not affect any spec in this change.
- Exact bounded-parallelism default for the execute stage - to be tuned empirically against real NVMe hardware during implementation; any reasonable default is spec-compliant since specs do not mandate a specific concurrency level.
