## 1. Repository support for path lookup

- [x] 1.1 Add `ISnapshotRepository.GetPathsForContentHash(string hash)` returning the distinct relative paths with a `file_versions` row for that hash
- [x] 1.2 Implement it in `SqliteSnapshotRepository` using the existing `idx_file_versions_content_hash` index (`SELECT DISTINCT relative_path FROM file_versions WHERE content_hash = @hash`)
- [x] 1.3 Add a repository test asserting the method returns every path referencing a hash, including a path whose only reference is a historical (non-current) version

## 2. Integrity check service

- [x] 2.1 Create `Vara.Application.Integrity.IntegrityCheckService`, mirroring `PruneService`'s constructor shape (`ISnapshotRepository`, `IContentStore`)
- [x] 2.2 Implement the check: compute `repository.GetAllReferencedContentHashes()`, then for each hash determine missing (absent from `contentStore.ListAllStoredHashes()`/`HasContent`), and - unless `quick` - re-hash present blobs via `contentStore.OpenRead(hash)` and `IHasher.ComputeHash`, comparing against the hash itself, collecting any mismatch as corrupt
- [x] 2.3 Compute orphaned blobs as `ListAllStoredHashes() - GetAllReferencedContentHashes()`
- [x] 2.4 For each missing/corrupt hash, call `GetPathsForContentHash` to attach affected paths to the result, capping the listed paths per hash with a "+N more" summary when the list is long
- [x] 2.5 Report progress via `IProgress<T>` (blobs checked so far / total referenced blobs), consistent with `PruneProgress`'s shape
- [x] 2.6 Add `Vara.Application.Tests` covering: all-intact (no findings), a missing blob, a corrupted blob (full mode only), quick mode not detecting a corrupted-but-present blob, and an orphaned blob reported without being deleted

## 3. CLI command

- [x] 3.1 Create `src/Vara.Cli/Commands/CheckCommand.cs` with `--profile` (required), `--config`, and `--quick` options, following `PruneCommand`'s structure (profile resolution, service construction via `ProfileServiceFactory`, live vs. plain progress display per `OutputMode.IsLiveCapable`)
- [x] 3.2 Register the command in `src/Vara.Cli/Program.cs` alongside the existing commands
- [x] 3.3 Report results (counts of checked/missing/corrupt/orphaned, plus affected paths for missing/corrupt) via a new `CheckOutcomeReporter`, following `PruneOutcomeReporter`'s style
- [x] 3.4 Return `ExitCodes.PartialFailure` when any missing or corrupt blob was found, `ExitCodes.Success` otherwise
- [x] 3.5 Add `Vara.Cli.Tests` covering the command's exit code for the found-problems and no-problems cases, and that `--quick` is passed through to the service

## 4. Documentation and verification

- [x] 4.1 Add `vara check` to the command reference table in `README.md`
- [x] 4.2 Run `cd src; dotnet test ..\tests\Vara.Application.Tests` and verify `IntegrityCheckService` tests pass
- [x] 4.3 Run `cd src; dotnet test ..\tests\Vara.Cli.Tests` and verify `CheckCommand` tests pass
- [x] 4.4 Confirm each scenario in the `backup-integrity` delta spec is covered by a test from Tasks 2-3
