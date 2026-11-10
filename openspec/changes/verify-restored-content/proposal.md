## Why

A restore copies a stored blob to the destination and never checks that what it copied is the content the manifest recorded for that version. A damaged blob (see the change `durable-blob-store` for one way that happens, and `vara check` for the detector) is restored without any signal, so the user gets a wrong file believing it is the recovered one. The restore already reads every byte through a pass that could hash them at almost no extra cost, and a restore exists precisely to be trusted when something has gone wrong.

## What Changes

- A restore verifies the bytes it writes against the content hash recorded for the version, before the restored file replaces anything at the destination.
- A file that does not match is not written: the destination stays exactly as it was, the file is reported as failed with a reason that says the stored content does not match what was recorded, and a directory restore continues with its other files and ends as a partial failure, like any per-file failure today.
- Scope is the `restore` command only. `show` and `diff` read blobs too but are not changed (see design.md).

No new option, no change to output for a restore that verifies.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `snapshot-history`: adds a requirement that restored content is verified against its recorded hash and that a mismatch is a per-file failure that leaves the destination untouched.

## Impact

- `src/Vara.Engine/Runs/RestoreRun.fs` (`writeFile`), and one small exception type next to the content store's existing one or in the same file
- `tests/Vara.Engine.Tests/Runs/HistoryEngineTests.fs` (new scenario tests; the file already has the restore effect helpers) and, for the single-file message, `tests/Vara.Cli.Tests/Commands/RestoreCommandTests.fs`
- Cost: hashing the restored bytes with XxHash128 while copying them. The copy is disk bound; the hash is not expected to be visible.
