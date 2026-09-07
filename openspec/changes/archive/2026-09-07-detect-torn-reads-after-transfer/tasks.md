## 1. Implement post-transfer stat verification

- [x] 1.1 In `BackupExecutor.ExecuteTransfer`, after `contentStore.StoreFromStream` completes (and
      before `contentStore.PlaceAtMirrorPath` runs, so a mismatch never places content at the
      mirror path), re-stat `operation.SourceAbsolutePath` (size and `LastWriteTimeUtc`) and
      compare against `operation.Size`/`operation.SourceModifiedAt`
- [x] 1.2 On a match, record the manifest entry exactly as today; verify via unit test that
      behavior for an unchanged file is unaffected
- [x] 1.3 On a mismatch, route to the same failure handling used for
      `IOException`/`UnauthorizedAccessException` (increment `Counts.Failed`, add to
      `failedPaths`, skip `RecordFileVersion` and any mirror placement side effects that would
      otherwise be treated as successful) and verify via unit test that no manifest row is written
      and the path appears in the run's failed-paths list
- [x] 1.4 Handle the re-stat call itself throwing (for example the file was deleted immediately
      after being read) by treating that as a failure too, and verify with a unit test

## 2. Tests

- [x] 2.1 Add a `BackupExecutor` test that modifies a file's content and modification time between
      constructing its `PlannedOperation` and executing the transfer, and verify the operation is
      reported as failed with no manifest entry written
- [x] 2.2 Add a test verifying such a file is re-selected for transfer by the incremental change
      detection logic on a subsequent run (recorded manifest state is unchanged, so the file's
      current stat still differs from it)
- [x] 2.3 Add a regression test confirming a normal, unmodified-during-transfer file still records
      its manifest entry with the scan-time modification time exactly as before
- [x] 2.4 Run the full `Vara.Application.Tests` (or equivalent) suite and verify all tests pass

## 3. Verification

- [ ] 3.1 Manually run a backup against a test file that is edited by a separate process mid-run
      (or simulate via a short delay + edit script) and confirm the run reports it as a failed
      path, and a following run successfully captures its latest content (skipped by user
      decision; equivalent behavior is covered by automated tests 2.1/2.2)
