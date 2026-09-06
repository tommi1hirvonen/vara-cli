## Why

`RestoreCommand` explicitly rejects `--out`+`--in-place` and `--recursive`+`--version` when both are
given, but never checks `--at`+`--version` together. When a user supplies both, `SnapshotHistoryService.ResolveVersion`
silently prefers `versionId` and ignores `asOf` with no warning, so the command behaves as if the
user's `--at` value had no effect - a silent footgun inconsistent with how every other mutually
exclusive flag pair in this command is already handled.

## What Changes

- `restore` (and `show`/`diff`, which resolve versions the same way) reject `--at` and `--version`
  given together with a clear error, before any version resolution is attempted, matching the
  existing `--out`/`--in-place` and `--recursive`/`--version` validation pattern.
- `ResolveVersion` is no longer reachable with both a version id and an "as of" date at once for
  a single logical resolution; callers validate this upfront instead of relying on silent
  precedence.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `snapshot-history`: the "Restore a file at a given version or date" requirement gains an explicit
  mutual-exclusion rule for `--at` and `--version`, so supplying both is rejected with a clear error
  instead of one silently overriding the other.

## Impact

- **Code**: `src/Vara.Cli/Commands/RestoreCommand.cs`, `src/Vara.Cli/Commands/ShowCommand.cs`,
  `src/Vara.Cli/Commands/DiffCommand.cs` (wherever both `--at` and `--version` are parsed for the
  same resolution).
- **Tests**: CLI-level tests covering the new rejected combination for each affected command.
- No changes to on-disk manifest format or unaffected flag combinations.
