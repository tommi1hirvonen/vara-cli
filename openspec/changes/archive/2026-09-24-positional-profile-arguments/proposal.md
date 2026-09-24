## Why

In an earlier change, all commands accepting a profile argument were shifted to `--profile <name>` for uniform CLI syntax. For profile-level commands (`backup`, `prune`, `check`, `snapshots`) that do not accept a file or directory path argument, requiring `--profile` adds unnecessary verbosity without any disambiguation benefit. Reverting these commands to accept a positional profile argument makes them shorter, more ergonomic, and aligned with standard CLI conventions (`vara backup default`).

## What Changes

- **BREAKING**: `vara backup` changes its profile parameter from a required `--profile <name>` option to a required positional `<profile>` argument (`vara backup <profile> [--dry-run] [--json]`). The `--profile` option is no longer accepted.
- **BREAKING**: `vara prune` changes its profile parameter from a required `--profile <name>` option to a required positional `<profile>` argument (`vara prune <profile> [--yes]`). The `--profile` option is no longer accepted.
- **BREAKING**: `vara check` changes its profile parameter from a required `--profile <name>` option to a required positional `<profile>` argument (`vara check <profile> [--quick]`). The `--profile` option is no longer accepted.
- `vara snapshots` accepts an optional positional `[profile]` argument (`vara snapshots [profile]`). When omitted, it continues to auto-detect the profile from the working directory by searching upward for `.vara/profile.db`. The `--profile` option is no longer accepted.
- Path- and directory-targeting commands (`history`, `restore`, `show`, `diff`, `browse`, `deleted`) remain unchanged: they keep `<path>` or `[directory]` as their positional argument and `--profile <name>` as an optional option, preserving working-directory auto-detection when executed from inside a mirror.
- `vara profiles` remains unchanged: it does not accept a profile argument and opens the interactive profile editor TUI.
- User-facing documentation (`README.md`) is updated to reflect the new command syntax.

## Capabilities

### New Capabilities
<!-- None -->

### Modified Capabilities
- `backup-execution`: `vara backup` takes the profile as a required positional argument (`vara backup <profile>`) instead of `--profile <name>`.
- `backup-integrity`: `vara check` takes the profile as a required positional argument (`vara check <profile>`) instead of `--profile <name>`.
- `retention-pruning`: `vara prune` takes the profile as a required positional argument (`vara prune <profile>`) instead of `--profile <name>`.
- `snapshot-history`: `vara snapshots` accepts an optional positional argument (`vara snapshots [profile]`) instead of `--profile <name>`.

## Impact

- CLI command implementations in `src/Vara.Cli/Commands/`: `BackupCommand.cs`, `PruneCommand.cs`, `CheckCommand.cs`, and `SnapshotsCommand.cs`.
- CLI unit tests in `tests/Vara.Cli.Tests/Commands/`: `BackupCommandTests.cs`, `PruneCommandTests.cs`, and `CheckCommandTests.cs`.
- Specifications in `openspec/specs/`: `backup-execution`, `backup-integrity`, `retention-pruning`, and `snapshot-history`.
- Project documentation in `README.md`.
