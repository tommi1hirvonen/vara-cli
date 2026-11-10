## Why

A backup writes each file's content to a temporary file and renames it to its content-derived name in the version store, without flushing the temporary file to disk. After a power loss, NTFS can keep the rename while losing the data, leaving a truncated blob under a valid name. Later backups find that name already present and deduplicate against it forever, and every mirror file hard-linked to it is damaged too. The README promises "crash-safe atomic writes"; the restore and configuration writers already flush before the rename, and the content store, which matters most, does not.

## What Changes

- A blob's content is flushed to persistent storage before it takes its content-derived name in the version store.
- When storing content whose name is already present, a present blob whose size differs from the content just read is repaired in place instead of being trusted. In place, so that every mirror entry hard-linked to the damaged blob sees the repaired content. Check and repair run under a lock per content name, so that parallel transfers of the same content never repair the blob at the same time, and none reads it while it is being rewritten.
- The flush is an effect a test can observe, in the same way hard-link creation already is.

No command-line, output, configuration or manifest-schema change. The repair is opportunistic: it happens when the same content is stored again. Finding damage in blobs that nothing re-stores stays with `vara check`, and an explicit repair command is a separate follow-up, not part of this change.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: adds two requirements, one on the completeness and durability of stored content, one on repairing a present blob that is shorter or longer than its content.

## Impact

- `src/Vara.Engine/Storage/ContentStore.fs` (`StoreLayout` gains the flush function and the lock table, `layout`, `storeFromStream`)
- `tests/Vara.Engine.Tests/Storage/ContentStoreTests.fs` (new scenario tests)
- Cost: one flush of the file's data per stored blob, on the transfer path. This is the main trade-off; see design.md for how it is measured. Backups that find all files unchanged store nothing and are unaffected.
