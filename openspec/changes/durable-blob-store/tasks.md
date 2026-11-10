## 1. Tests first

- [ ] 1.1 In `tests/Vara.Engine.Tests/Storage/ContentStoreTests.fs`, add a module `Stored content is complete and durable under its content name` with the test `Store interrupted while the content is being written`: store from a stream that throws after some bytes, assert the exception propagates, that nothing exists under the version store for any hash, and that the existing orphaned-staging cleanup still removes the staging file. Verify it passes on the current code (it pins behaviour).
- [ ] 1.2 In that module add `Content reaches persistent storage before it takes its name` using `{ layout with Flush = ... }` (the seam of task 2.1): the replacement records whether the blob existed when it ran, and the test asserts it ran once and the blob did not yet exist. Verify it fails to compile or fails until task 2.1 and 2.2 are done.
- [ ] 1.3 Add a module `A damaged stored blob is repaired when its content is stored again` with the tests `Present blob is shorter than the content it is named for`, `Mirror entries linked to a damaged blob see the repaired content` (place a file by hard link, truncate the blob through its read-only-cleared handle, store the same content again, assert the mirror entry has the full content), and `Present blob of the expected size is left untouched` (assert the blob's last-write time and bytes are unchanged and the staging file is gone). Verify they fail on the current code, except the last.
- [ ] 1.4 In the same module add `Parallel stores of the same damaged content`: repeat 50 times, each time with a fresh target, a blob shorter than its content, and 8 concurrent `storeFromStream` calls of that content (then, for one iteration, `placeAtMirrorPath` after each store with hard links off). Assert no call throws, the blob holds the complete content, and every mirror file placed holds the complete content. Verify it fails (an exception or a short file in some iteration) against an implementation of task 2.3 without the lock.

## 2. Implementation

- [ ] 2.1 In `ContentStore.fs`, add `Flush: FileStream -> unit` to `StoreLayout`, set to a real flush (`stream.Flush true`) in `layout`, and `HashLocks: obj array` (256 objects, created in `layout`), indexed by the blob's two-character directory name. Verify the solution builds and no other record literal of `StoreLayout` needs changing.
- [ ] 2.2 In `storeFromStream`, after the tee has been consumed, call `sink.Flush()` and then `store.Flush tempFile` before the streams are disposed. Verify tests 1.1 and 1.2 pass.
- [ ] 2.3 In `storeFromStream`, when the blob exists, take the lock for its stripe and, inside it, compare the blob's length with the size read. Equal: delete the staging file as before. Different: clear the read-only attribute (remembering it), overwrite the blob in place through a `FileMode.Create` stream from the staging file, flush it with `store.Flush`, delete the staging file, and restore the read-only attribute in a `finally`; on a failure delete the staging file and let the exception propagate. Do not lock the branch where the blob does not exist. Verify the three tests of task 1.3 and the test of task 1.4 pass, including the hard-link one.
- [ ] 2.4 Update the doc comment of `storeFromStream` to describe the flush, the repair and why it is serialized per content name. Verify the build has no warnings.

## 3. Measurement

- [ ] 3.1 Measure the cost as described in design.md (before and after, many small files, an external drive if one is available; allocations if timings are noisy) and record the numbers in the Risks section of design.md. If the slowdown looks unacceptable, stop and report it instead of changing the approach.

## 4. Verification

- [ ] 4.1 Run `dotnet build Vara.slnx` (no warnings) and `dotnet test Vara.slnx`, and verify everything passes. The test `History command corpus` in `Vara.Cli.Tests` fails intermittently on the untouched baseline too (a test-host encoding issue unrelated to this change); note it rather than fixing it here.
- [ ] 4.2 Stop and leave the working tree uncommitted for review. Do not archive or sync the change until the author has reviewed and committed the implementation.
