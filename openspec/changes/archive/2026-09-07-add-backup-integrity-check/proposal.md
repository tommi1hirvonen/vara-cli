## Why

Vara is a plain, uncompressed mirror plus a manifest of historical versions - there is currently no way to ask "is my backup actually intact and restorable?" Change detection only ever compares the *source*'s size/mtime against the manifest to decide what to re-copy; it says nothing about whether content already written to the *target* is still what the manifest says it is. A disk error, filesystem corruption, or an out-of-band edit to the target could silently corrupt or remove a blob the manifest still thinks is available, and nothing would surface that until a restore or diff tried to read it and failed (or, worse, silently produced wrong content if the corruption doesn't fail the read). There is no `vara check`/`vara verify` command to answer this.

This proposal is scoped to target-side integrity only: verifying that content physically stored in the target still matches what the manifest expects. It does not attempt to detect source-side drift (a source file changing without its mtime/size changing) - that is a different question with a different, much more expensive answer (re-reading every source file every run, which defeats incremental backup), and is explicitly out of scope here.

## What Changes

- New `vara check --profile <name> [--config <path>] [--quick]` command that:
  - Re-hashes every content blob referenced by any snapshot in the profile's manifest (full history, not just the current live mirror) and compares it against the hash the manifest recorded for it, reporting any blob that is missing or whose content no longer matches (full mode, the default).
  - With `--quick`, checks only that each referenced blob is physically present in the store (no content read/re-hash), trading thoroughness for speed.
  - Reports blobs referenced by the manifest but absent from the store ("missing") and blobs whose re-hashed content does not match the manifest ("corrupt") as the actionable findings, identifying the affected file path(s) for each.
  - Separately reports blobs physically present in the store but not referenced by any snapshot ("orphaned") as informational only - these are already handled by `vara prune`'s existing garbage collection and are not deleted by `vara check`.
  - Exits with a non-zero, scriptable status when any missing or corrupt blob is found, so the command is usable in an automated health-check.
- No change to backup, restore, or prune behavior; this is a new, purely diagnostic command.

## Capabilities

### New Capabilities
- `backup-integrity`: verifying that content physically stored in a profile's target still matches the manifest's record of it, independent of and separate from source-side change detection.

### Modified Capabilities
(none)

## Impact

- New CLI command: `src/Vara.Cli/Commands/CheckCommand.cs`
- New application service coordinating the check (e.g. `src/Vara.Application/Integrity/IntegrityCheckService.cs`)
- `src/Vara.Core/Abstractions/ISnapshotRepository.cs` / `IContentStore.cs`: both already expose everything needed to enumerate referenced vs. stored hashes (`GetAllReferencedContentHashes`, `ListAllStoredHashes`, `OpenRead`); a new repository method to map an affected hash back to the file path(s) that reference it is needed for actionable reporting (see design.md)
- `src/Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs` (new path-lookup query, using the existing `content_hash` index)
