## 1. `PruneService` preview

- [ ] 1.1 Add a side-effect-free `int CountEligibleForRemoval(Profile profile)` method to
      `PruneService` (`src/Vara.Application/Retention/PruneService.cs`) that throws
      `RetentionPolicyNotConfiguredException` under the same condition as `Prune`, otherwise
      calls `repository.ListSnapshots()` and `_evaluator.DetermineEligibleForRemoval` (the same
      calls already made inside `Prune`) and returns the eligible count, without acquiring the
      run lock and without deleting anything. Verify the solution builds.
- [ ] 1.2 Add XML doc comments cross-referencing `Prune`, noting this method is a read-only
      preview meant to be called before it to decide whether to prompt for confirmation, and
      that its result can be stale by the time `Prune` actually runs (see design.md's
      "Preview/execute race" risk). Verify the solution builds.

## 2. CLI: `--yes`/`-y` option and confirmation flow

- [ ] 2.1 Add a `--yes`/`-y` boolean option to `PruneCommand`
      (`src/Vara.Cli/Commands/PruneCommand.cs`), following the same `System.CommandLine` option
      style as `RestoreCommand`'s `--force`. Verify `vara prune --help` lists the new option.
- [ ] 2.2 Before calling `pruneService.Prune(profile)`, call the new
      `pruneService.CountEligibleForRemoval(profile)`. If the count is zero, or `--yes`/`-y` was
      passed, proceed directly to `Prune` unchanged. Verify by manually running `prune` on a
      profile with nothing eligible for removal and confirming no prompt appears.
- [ ] 2.3 When the count is greater than zero and `--yes`/`-y` was not passed, branch on
      `Console.IsInputRedirected`: if `true` (non-interactive), report a clear error via
      `OutcomeStyle.WriteLineError` (through `StandardError.Console`) explaining that `N`
      snapshot(s) are eligible for removal and an explicit `--yes`/`-y` override is required,
      and return a non-zero exit code without calling `Prune`. Verify manually with stdin
      redirected (e.g. `... | vara prune myprofile`) against a profile with an eligible
      snapshot.
- [ ] 2.4 If `false` (interactive), prompt via `StandardError.Console.Confirm` (mirroring
      `RestoreCommand`'s prompt), stating the eligible count and noting that unreferenced
      content will also be freed, defaulting to `false`. If confirmed, call `Prune` and report
      the outcome via `PruneOutcomeReporter.Report` as today; if declined, set a `cancelled` flag
      (mirroring `RestoreCommand`'s pattern) instead of calling `Prune`. Verify manually in an
      interactive terminal: once answering "y" (snapshots are removed and the normal outcome is
      reported) and once answering "n" or pressing enter (no snapshots or content are removed).
- [ ] 2.5 When `cancelled` is set, report a neutral-style cancellation message via
      `OutcomeStyle.WriteLineNeutral` (e.g. "Prune cancelled: no snapshots removed.") and return
      exit code `0`, mirroring `RestoreCommand`'s "Restore cancelled: destination not
      overwritten." message. Verify manually that declining the prompt prints this message and
      `echo $LASTEXITCODE` (or `$?`) reports `0`.

## 3. Tests

- [ ] 3.1 Add `PruneServiceTests` cases (`tests/Vara.Application.Tests/Retention/
      PruneServiceTests.cs`) covering `CountEligibleForRemoval`: throws
      `RetentionPolicyNotConfiguredException` for a profile with no retention policy (mirroring
      the existing `Prune` test of the same condition); returns `0` when no snapshots are
      eligible; returns the correct count when some snapshots are eligible; and does not modify
      `repository.ListSnapshots()` or `contentStore`'s stored content as a result of being
      called (no side effects).
- [ ] 3.2 Run `dotnet test tests/Vara.Application.Tests` and verify all tests pass, including
      the new cases from 3.1 alongside the existing `PruneServiceTests` cases.
- [ ] 3.3 Manually exercise the full CLI flow end to end against a real profile directory
      (matching the level of manual verification already used for `RestoreCommand`'s
      confirmation logic, which likewise has no automated CLI-layer test): confirm prompt text,
      confirm/decline/`--yes`/`-y`/non-interactive-rejection paths, and confirm a declined or
      rejected run leaves the manifest and content store byte-for-byte unchanged (e.g. compare a
      checksum of the profile's database and version-store directory before and after).
