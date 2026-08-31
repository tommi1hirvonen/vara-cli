## 1. Manifest schema and models

- [x] 1.1 Add nullable `QuickHash` (`string?`) and `QuickHashScheme` (`int?`) to `FileVersionRecord` and `CurrentFileState` in `src\Vara.Core\Snapshots\FileVersionRecord.cs`, and define the current scheme constant (e.g. window size `W` = 64 KB) alongside them; verify with `dotnet build`.
- [x] 1.2 Extend `ISnapshotRepository.RecordFileVersion` (`src\Vara.Core\Abstractions\ISnapshotRepository.cs`) with optional quick-hash parameters, and update `SqliteSnapshotRepository` (`src\Vara.Infrastructure\Snapshots\SqliteSnapshotRepository.cs`) to add the two new nullable columns to the `file_versions` table and persist/read them; verify with `dotnet test tests\Vara.Infrastructure.Tests --filter FullyQualifiedName~SqliteSnapshotRepositoryTests`.
- [x] 1.3 Update `SqliteSnapshotRepository.GetCurrentState()` to populate `CurrentFileState.QuickHash`/`QuickHashScheme` from the new columns; add a test asserting a recorded quick hash round-trips through `GetCurrentState()`.
- [x] 1.4 Update all `ISnapshotRepository` implementations used by tests (`FakeSnapshotRepository` in `tests\Vara.Application.Tests\Backup\Fakes.cs`, the fake in `tests\Vara.Core.Tests\Abstractions\PortAbstractionsTests.cs`) to accept and round-trip the new parameters; verify the full solution still builds with `dotnet build`.

## 2. Streaming quick-hash/full-hash helper

- [x] 2.1 Add a helper that computes a quick hash (first `W` bytes) and a full hash from a single forward stream traversal, tagged with the current `QuickHashScheme`, supporting early termination when the caller determines no further reading is needed; unit test it against streams shorter than, exactly equal to, and longer than `W` bytes.
- [x] 2.2 Unit test that the full hash produced by this helper matches `IHasher.ComputeHash` on the same content byte-for-byte, so confirmed matches remain exactly as accurate as today.

## 3. Recording quick hashes when content is stored

- [x] 3.1 Wire the transfer path (`BackupExecutor.ExecuteTransfer` in `src\Vara.Application\Backup\BackupExecutor.cs`, and/or `FileSystemContentStore.StoreFromStream`) to compute the quick hash from the same bytes already streamed when storing new/changed content, and pass it to `RecordFileVersion` for `Add`/`Change` operations; verify with a `BackupExecutor` test asserting a `QuickHash` is recorded for a newly added file.
- [x] 3.2 For `Move` operations, carry the matched candidate's existing `QuickHash`/`QuickHashScheme` forward to the new path's `RecordFileVersion` call in `BackupExecutor.ExecuteMoveOrDelete` (no re-hash, content is provably identical); verify with a test asserting a moved file's new manifest row has the same `QuickHash` as its pre-move row.

## 4. BackupPlanner: parallel and early-abort move detection

- [x] 4.1 Replace `TryHashFile` (`src\Vara.Application\Backup\BackupPlanner.cs`) with the streaming helper from Task 2.1, early-aborting the read once the quick hash is available if no still-viable same-size candidate's recorded `QuickHash` (under the current `QuickHashScheme`) matches, and otherwise continuing to read to EOF for a full-hash confirmation; verify with a unit test asserting a non-matching same-size candidate is ruled out without reading the remainder of the file's content (e.g. via a byte-counting stream wrapper).
- [x] 4.2 Restructure `BackupPlanner.Plan`'s move-detection loop into a parallel signature-computation phase (`Parallel.ForEach` over size-matched `Added` entries populating a `ConcurrentDictionary<string, ...>` keyed by relative path, sized consistently with `BackupExecutor`'s existing `MaxDegreeOfParallelism` convention) followed by the existing serial matching/consumption loop, now reading precomputed signatures instead of hashing inline; the matching/consumption logic and iteration/tie-break order must remain unchanged.
- [x] 4.3 Add a test with multiple size-matched candidates (including a tie: two deleted candidates of the same size and same content) confirming the parallelized planner produces identical results (same moves detected, same tie-breaking winner) to the pre-change serial behavior.
- [x] 4.4 Add a test covering the "recorded signature unavailable" fallback: a same-size deleted candidate with no `QuickHash` recorded (or a mismatched `QuickHashScheme`) still gets a full-content read/compare rather than being silently skipped or misclassified.
- [x] 4.5 Verify all existing `BackupPlannerTests` pass unmodified, confirming move-detection's observable outcome is unchanged: `dotnet test tests\Vara.Application.Tests --filter FullyQualifiedName~BackupPlannerTests`.

## 5. Cross-cutting verification

- [x] 5.1 Build the full solution and run the full test suite to confirm no regressions: `dotnet build` then `dotnet test`.
- [x] 5.2 Confirm the new `backup-execution` spec scenarios ("Same-size, differing-content candidate ruled out cheaply", "Full content match still required to confirm a move", "Recorded signature unavailable for a candidate") each have a corresponding passing test from the tasks above, and note the mapping in the PR/commit description.

### Scenario-to-test mapping (for task 5.2)
- "Same-size, differing-content candidate ruled out cheaply" -> `BackupPlannerTests.A_same_size_candidate_ruled_out_by_quick_hash_is_never_fully_read`
- "Full content match still required to confirm a move" -> `BackupPlannerTests.A_same_size_candidate_matched_by_quick_hash_is_confirmed_via_a_full_read`
- "Recorded signature unavailable for a candidate" -> `BackupPlannerTests.A_candidate_missing_a_recorded_quick_hash_still_gets_a_full_read_and_correct_match`
