## 1. Factory changes

- [x] 1.1 Add a `createIfMissing` parameter (default `true`) to `ProfileServiceFactory.CreateFor`, and verify a unit test confirms the default preserves today's eager-creation behavior
- [x] 1.2 Add an existence check to `SqliteSnapshotRepository`'s construction path (or a factory-level check) so a missing `profile.db` is not created when `createIfMissing` is `false`, and verify a test confirms no file is created
- [x] 1.3 Guard `FileSystemContentStore`'s directory creation behind the same flag, and verify a test confirms no `.vara\` directories are created when `createIfMissing` is `false`

## 2. Command call-site triage

- [x] 2.1 Update `SnapshotsCommand` and `BrowseCommand` to call the factory with `createIfMissing: false`, and verify each reports a clear "nothing recorded yet" outcome against a fresh profile without creating `.vara\`
- [x] 2.2 Review `DeletedCommand`, `HistoryCommand`, `RestoreCommand`, `DiffCommand`, `ShowCommand` and classify each as read-only or read-write; update read-only ones to `createIfMissing: false` and verify each with the same fresh-profile test
- [x] 2.3 Confirm `BackupCommand` and `PruneCommand` remain on `createIfMissing: true` (unchanged behavior), and verify existing tests for both still pass

## 3. Verification

- [x] 3.1 Run `openspec validate --change fix-readonly-command-side-effects --strict` and confirm it passes
- [x] 3.2 Run the full CLI command test suite and confirm no existing tests regress
