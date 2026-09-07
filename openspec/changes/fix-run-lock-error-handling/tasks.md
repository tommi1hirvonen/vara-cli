## 1. Reword the "already in progress" messages

- [x] 1.1 Update `BackupAlreadyRunningException`'s message to describe the shared target as busy instead of asserting the requesting profile itself is the one running, and verify by updating/inspecting `tests/Vara.Application.Tests/Backup/BackupPipelineTests.cs`'s existing assertion
- [x] 1.2 Update `PruneAlreadyRunningException`'s message the same way, and verify by updating/inspecting `tests/Vara.Application.Tests/Retention/PruneServiceTests.cs`'s existing assertion
- [x] 1.3 Update `IRunLock`'s doc comment (`src/Vara.Core/Abstractions/IRunLock.cs`) to reflect that it is shared by both backup and prune runs, not backup runs only

## 2. Handle lock-acquisition access-denied distinctly

- [x] 2.1 Add `RunLockAccessDeniedException` (new file, e.g. `src/Vara.Core/Concurrency/RunLockExceptions.cs`) carrying the target root, following the existing `*Exceptions.cs` per-domain convention
- [x] 2.2 Update `FileRunLock.TryAcquire` to catch `UnauthorizedAccessException` alongside the existing `IOException` catch, throwing `RunLockAccessDeniedException` instead of letting it propagate unhandled or returning `false`
- [x] 2.3 Add `RunLockAccessDeniedException` to the friendly-message pattern match in `src/Vara.Cli/Composition/ErrorReporting.cs` (`TryGetFriendlyMessage`), alongside `BackupAlreadyRunningException`/`PruneAlreadyRunningException`
- [x] 2.4 Add a `FileRunLockTests` case that makes the lock file's directory inaccessible (or otherwise forces `UnauthorizedAccessException` on open) and asserts `TryAcquire` throws `RunLockAccessDeniedException` rather than letting `UnauthorizedAccessException` propagate or returning `false`

## 3. Verify

- [x] 3.1 Run `cd src; dotnet test ..\tests\Vara.Infrastructure.Tests` and verify all `FileRunLockTests` pass
- [x] 3.2 Run `cd src; dotnet test ..\tests\Vara.Application.Tests` and verify `BackupPipelineTests`/`PruneServiceTests` pass with the updated message assertions
- [x] 3.3 Confirm both delta specs' new scenarios (shared-target message, lock-file-inaccessible) are satisfied by the implementation and covered by tests from Tasks 1-2
