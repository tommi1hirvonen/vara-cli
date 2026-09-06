## 1. CLI validation

- [ ] 1.1 In `RestoreCommand.cs`, add a check rejecting `--at` and `--version` given together (mirroring the existing `--out`/`--in-place` check), and verify a unit/CLI test exercising both flags together reports the new clear error and performs no restore
- [ ] 1.2 In `ShowCommand.cs`, add the same `--at`/`--version` mutual-exclusion check, and verify a test covers both flags together
- [ ] 1.3 In `DiffCommand.cs`, add the same check for each side's `--at`/`--version` pair (left and right), and verify a test covers both flags together on at least one side

## 2. Spec verification

- [ ] 2.1 Run `openspec validate --change fix-restore-at-version-mutual-exclusion --strict` and confirm it passes
- [ ] 2.2 Run the full restore/show/diff command test suites and confirm all existing tests still pass alongside the new ones
