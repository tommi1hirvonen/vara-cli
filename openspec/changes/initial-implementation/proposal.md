## Why

Vara is a new CLI backup tool. Today the repository only has an empty clean-architecture solution stub (Core/Application/Infrastructure/Cli) and a sample profile config format — there is no working backup functionality yet. This change delivers the first end-to-end implementation: a profile-driven, incremental, deduplicated backup engine whose target output is a literal, browsable, one-to-one mirror of the source (so a user can migrate machines by simply copying the mirror folder), while still keeping full version history for point-in-time recovery.

## What Changes

- Add profile configuration loading from `~/.vara/profiles.yml`: named profiles, each with a target root and one or more sources (path, recursive flag, exclude list, optional glob patterns), plus a per-profile tiered retention policy.
- Add a `vara backup <profile>` command implementing the core pipeline: scan source(s) -> diff against a SQLite manifest -> build a byte-weighted execution plan -> execute (copy/hardlink) -> commit a snapshot record. Unchanged files cost a manifest lookup only; the run completes quickly when nothing changed.
- Add a content-addressed version store (`<target>\.vara\versions\<hash>`) with the live mirror (`<target>\<profile-relative-path>\...`) hardlinked into it when the target filesystem supports hardlinks, detected via a startup capability probe. Automatic graceful degradation to real file copies on filesystems without hardlink support (e.g. exFAT/FAT32) - dedup of historical content in the store still applies, only live-mirror byte sharing is lost.
- Add move/rename detection (matching content hash at a new path with the old path missing) that relocates files via rename/re-link instead of re-copying bytes, independent of hardlink support.
- Add byte-weighted progress reporting: the plan step computes real total bytes to transfer before execution starts, so the CLI shows genuine bytes-copied/total progress, throughput, and ETA rather than file/directory counts.
- Add snapshot/version history browsing and restore: list snapshots for a profile with timestamps/stats, view a file's version history, and extract a file's content as of a given version or date.
- Add tiered retention (`keep_daily`/`keep_weekly`/`keep_monthly`/`keep_yearly`, newest-per-bucket) evaluated by a `vara prune <profile>` command, which removes expired snapshot records and garbage-collects version-store content no longer referenced by any remaining snapshot or the live mirror.
- Crash/interruption safety: atomic per-file writes (temp file + rename), a per-profile run lock to prevent concurrent runs against the same target, a snapshot `running`/`complete`/`failed` status, and cleanup of orphaned temp files at the start of the next run.
- Native AOT-published CLI (`Vara.Cli`, already AOT-enabled) built on `System.CommandLine` for the command surface and `Microsoft.Extensions.Hosting` for dependency injection, following the existing clean-architecture layering (Core: domain + ports, Application: use cases/pipeline orchestration, Infrastructure: SQLite manifest, filesystem scanner, hardlink probe, content store, YAML config loader, Cli: command definitions/composition root).

**Explicitly out of scope for this change** (documented limitations, not gaps): VSS/shadow-copy handling for locked/in-use files (failures are logged and skipped), following symlinks/junctions (their existence is recorded, not their targets), NTFS-specific fast-path change detection such as the USN Journal (baseline is a full directory walk diffed against the manifest), NTFS ACL/alternate-data-stream/permission fidelity, and non-Windows platforms.

## Capabilities

### New Capabilities
- `profile-config`: Loading and validating the `~/.vara/profiles.yml` file - profiles, sources, target roots, exclude/glob rules, and per-profile retention policy.
- `backup-execution`: The incremental scan/diff/plan/execute pipeline, content-addressed dedup with hardlink capability detection and degradation, move detection, atomic mirror updates, snapshot recording, and crash/concurrency safety.
- `progress-reporting`: Byte-weighted execution planning and live CLI progress/throughput/ETA output during a backup run.
- `snapshot-history`: Browsing recorded snapshots/versions for a profile and restoring a file's content as of a given version or date.
- `retention-pruning`: Evaluating the tiered retention policy and the `vara prune <profile>` command that expires old snapshots and garbage-collects unreferenced version-store content.

### Modified Capabilities
- None - this is the first implementation; no existing specs exist yet.

## Impact

- New `Vara.Core` domain model and ports (e.g. `Profile`, `Source`, `Snapshot`, `FileRecord`, `IChangeDetector`, `IContentStore`, `ISnapshotRepository`).
- New `Vara.Application` use cases orchestrating the backup pipeline, history queries, restore, and prune.
- New `Vara.Infrastructure` implementations: SQLite manifest (via `Microsoft.Data.Sqlite`), filesystem scanner, hardlink capability probe and content store, YAML profile config loader, hashing.
- New `Vara.Cli` command definitions and `HostBuilder` composition root, replacing the current `Hello, World!` stub in `Program.cs`.
- New dependencies to be added via `Directory.Packages.props`: `System.CommandLine`, `Microsoft.Extensions.Hosting`, `Microsoft.Data.Sqlite`, a fast non-cryptographic hashing library, and a YAML parser (AOT/trimming compatibility to be confirmed in design).
- No existing behavior is modified; this is additive to a greenfield project.
