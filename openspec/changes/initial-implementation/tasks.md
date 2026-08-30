## 1. Project & Dependency Setup

- [ ] 1.1 Add `System.CommandLine`, `Microsoft.Extensions.Hosting`, `Microsoft.Data.Sqlite`, a YAML parsing library, and a non-cryptographic hashing package (e.g. `System.IO.Hashing`) to `Directory.Packages.props` and reference them from the appropriate projects; verify `dotnet build` succeeds across the solution.
- [ ] 1.2 Create per-layer test projects mirroring `src/` (`tests/Vara.Core.Tests`, `tests/Vara.Application.Tests`, `tests/Vara.Infrastructure.Tests`), each referencing only its corresponding `src/` project and using xUnit; verify `dotnet test` runs successfully across all three (even with zero tests initially).
- [ ] 1.3 Verify Native AOT publish still succeeds for `Vara.Cli` after adding dependencies via `dotnet publish -c Release`, checking for trimming/AOT warnings introduced by the new packages.

## 2. Core Domain Model & Ports (Vara.Core)

- [ ] 2.1 Define domain types: `Profile`, `Source` (path, recursive, excludes, globs), `RetentionPolicy` (daily/weekly/monthly/yearly counts), `Snapshot`, `FileVersionRecord`, and a file event kind (added/changed/moved/deleted/unchanged); verify with unit tests constructing valid instances and rejecting invalid ones.
- [ ] 2.2 Define ports: `IProfileConfigLoader`, `ISnapshotRepository` (manifest access), `IContentStore` (hash-addressed blob store abstraction exposing a hardlink-capability flag), `IHasher`, `IFileSystemScanner`, `IRunLock`; verify the interfaces compile and that `Vara.Application`/`Vara.Infrastructure` can reference them without circular project references.

## 3. Profile Configuration Loading (profile-config)

- [ ] 3.1 Implement loading of `~/.vara/profiles.yml` into the `Profile` domain model in `Vara.Infrastructure`, confirming the chosen YAML approach is AOT/trimming-safe (source-generated context or hand-written mapping); verify with a unit test loading the shape from `docs/config-sample.yml`.
- [ ] 3.2 Implement validation: required fields (name, target, at least one source), duplicate profile name rejection, default `recursive = true` when omitted; verify with unit tests covering each validation scenario in the spec.
- [ ] 3.3 Implement clear, user-facing errors for a missing configuration file and an unknown profile name; verify with unit tests asserting the error content for both cases.

## 4. Manifest Storage (SQLite)

- [ ] 4.1 Design and implement the SQLite schema (`snapshots`, `file_versions` tables) and initialization logic for a new profile's database; verify with a test that a fresh database initializes with the expected schema.
- [ ] 4.2 Implement `ISnapshotRepository`: create/update snapshot rows (status transitions `running`/`complete`/`failed`), insert `file_versions` rows, query the current-state view (latest non-deleted row per path), query a file's version history, and query the snapshot list; verify with integration tests against a temporary SQLite database covering each query.
- [ ] 4.3 Implement the reference-counting query used by pruning (content hashes with zero remaining references across all snapshots, including tombstones, and the current live state); verify with a test seeding known references and asserting the correct unreferenced set is returned.

## 5. Content Store & Hardlink Capability (backup-execution)

- [ ] 5.1 Implement the hardlink capability probe (attempt and remove a throwaway hardlink under `.vara/<profile>/tmp`) executed at the start of each run; verify with a test on an NTFS path (expect success).
- [ ] 5.2 Implement `IContentStore`: stream-and-hash into `tmp`, resolve or create the canonical blob under `versions/<hash>`, then hardlink or copy it into the mirror path depending on the capability flag, skipping storage if the hash's blob already exists; verify with unit tests covering both the hardlink-capable and copy-fallback code paths.
- [ ] 5.3 Implement atomic replacement of mirror entries (rename over the existing entry, never an in-place overwrite) and cleanup of orphaned `tmp` files at run startup; verify with a test simulating a leftover `tmp` file from an interrupted run.

## 6. Backup Pipeline (backup-execution)

- [ ] 6.1 Implement the scan stage: recursive/non-recursive directory walk honoring excludes and glob patterns, recording (not following) symlinks/junctions/reparse points; verify with unit tests over a temporary directory tree covering excludes and a symlink.
- [ ] 6.2 Implement the diff stage: compare scanned entries against the manifest's current-state view by size and modification time, classifying each as unchanged/changed/added, and detect deletions (manifest entries with no corresponding scanned entry); verify with unit tests for each classification.
- [ ] 6.3 Implement move detection: match a changed/added entry's content hash against a hash that disappeared from its previous path in the same run, treating it as a move rather than a delete+add; verify with a unit test moving a file between two directories.
- [ ] 6.4 Implement the plan stage: aggregate the diff into an operation list with total bytes to transfer, excluding move/delete/hardlink-only operations from the byte total; verify with a unit test asserting the computed byte total reflects only genuinely transferred content.
- [ ] 6.5 Implement the execute stage: apply planned operations with bounded parallelism, isolate per-file failures (e.g. a locked file) so they are recorded without aborting the run, and write content via the atomic content-store path; verify with an integration test that includes an intentionally locked file and confirms the run still completes.
- [ ] 6.6 Implement the commit stage: finalize the snapshot row (stats, `status = complete`) only after every planned operation has resolved (success or recorded failure); verify with a test that an interrupted run leaves the snapshot row as `running`, not `complete`.
- [ ] 6.7 Implement the per-profile run lock preventing concurrent runs against the same target; verify with a test attempting to start a second concurrent backup and asserting it is refused with a clear message.
- [ ] 6.8 Add an integration test that runs a backup twice against an unchanged source and verifies the second run performs zero content transfer.

## 7. Progress Reporting (progress-reporting)

- [ ] 7.1 Implement live progress reporting in the CLI driven by the plan's byte total and the execute stage's bytes-written updates, displaying percentage, throughput, and ETA; verify by a manual run against a source containing a large file, observing continuous progress updates.
- [ ] 7.2 Implement the run summary output (added/changed/moved/deleted counts, bytes transferred, elapsed time, failed files) printed at the end of a backup run; verify with a unit test asserting the summary content given a known set of pipeline results.

## 8. Snapshot History & Restore (snapshot-history)

- [ ] 8.1 Implement a command listing recorded snapshots for a profile with timestamp and summary statistics; verify with an integration test seeding known snapshots and asserting the listed output.
- [ ] 8.2 Implement a command listing a file's recorded versions, most recent first, including when a version was superseded or deleted; verify with an integration test covering a file with multiple versions and a deletion.
- [ ] 8.3 Implement a command extracting historical file content (including a previously-deleted file) as of a given version or date to a user-specified location, without modifying the live mirror; verify with an integration test restoring a superseded version and a deleted file.
- [ ] 8.4 Implement clear error reporting for unknown paths and non-existent versions/dates in the history and restore commands; verify with unit tests for each error scenario in the spec.

## 9. Retention & Pruning (retention-pruning)

- [ ] 9.1 Implement the tiered bucketing algorithm (daily/weekly/monthly/yearly, newest-per-bucket) that determines which snapshots are eligible for removal; verify with unit tests covering bucket boundaries and configured counts.
- [ ] 9.2 Implement the `prune` command: apply the bucketing algorithm, delete eligible snapshot/file-version records, then run the unreferenced-content garbage collection to delete orphaned blobs; verify with an integration test asserting the expected snapshots and blobs remain after pruning a seeded history.
- [ ] 9.3 Implement the no-retention-policy-configured path that reports clearly and performs no deletions; verify with a unit test asserting no deletions occur and the expected message is produced.
- [ ] 9.4 Add an integration test confirming files currently present in the live mirror are untouched by a prune run.

## 10. CLI Composition Root

- [ ] 10.1 Replace the `Program.cs` stub with a `HostBuilder`-based composition root registering all `Vara.Core` ports to their `Vara.Infrastructure` implementations; verify `dotnet run -- --help` lists all commands.
- [ ] 10.2 Wire up `System.CommandLine` command definitions for the backup, snapshots, history, restore, and prune commands, each taking a profile name argument per the profile-config spec; verify each command's `--help` output and argument parsing with unit/integration tests.
- [ ] 10.3 Verify the fully wired CLI still publishes successfully as Native AOT (`dotnet publish -c Release`) and that the published, framework-independent executable runs the backup command end-to-end against a temporary profile.

## 11. End-to-End Verification

- [ ] 11.1 Manually exercise the full lifecycle - configure a profile, run backup twice (second run near-instant with no changes), modify/move/delete files, run backup again, browse snapshots/history, restore an old and a deleted file version, then prune - and confirm behavior matches every scenario described in the five specs.
