## 1. Tests first

- [x] 1.1 In `tests/Vara.Engine.Tests/Runs/HistoryEngineTests.fs`, add a module `Restored content is verified against its recorded hash` with the test `Stored content does not match its recorded hash`: back up a file, restore it to a destination that already holds a file using an `OpenBlob` that returns different bytes of the same length, and assert `Written = 0`, one `Failed` entry for the destination whose reason mentions that the stored content does not match, the destination content unchanged, and no other file beside it. Verify it fails on the current code (the damaged bytes are written).
- [x] 1.2 In the same module add `One damaged file in a directory restore`: back up three files, restore the directory with an `OpenBlob` that damages only one blob's bytes, and assert the other two are written, the damaged one is absent from the destination and listed in `Failed`, and `Stopped` is false. Verify it fails on the current code.
- [x] 1.3 In `tests/Vara.Cli.Tests/Commands/RestoreCommandTests.fs`, add (in a module with the same requirement title, since the single-file exit status is a command behaviour) a test with the scenario title `Stored content does not match its recorded hash` using that file's existing helpers: damage the blob on disk under the target's version store, run `restore --force`, and assert the hard-error exit code, an error line naming the file and the mismatch, and the destination unchanged. If the file's helpers cannot damage a blob simply, damage it directly with `File.WriteAllText` after clearing the read-only attribute. Verify it fails on the current code. Note that the two scenarios may live in different modules with the same name; the spec-coverage test accepts a test in any test assembly.

## 2. Implementation

- [x] 2.1 Add `RestoredContentMismatchException` (an `IOException` subtype with the expected and actual hashes and a message as described in design.md) to `src/Vara.Engine/Runs/RestoreRun.fs` above the module, or next to `MirrorPathEscapesTargetRootException` if that reads better. Verify the project builds.
- [x] 2.2 In `RestoreRun.writeFile`, replace `tee.CopyTo Stream.Null` with `Hashing.hashStreamReporting ignore tee`, compare with `write.Source` before `sink.Flush()`, and raise the exception on a mismatch. Update the function's doc comment to say the content is verified before it is moved into place. Verify tests 1.1 to 1.3 pass and the existing `Restore writes are atomic` tests still pass, including the progress-bytes assertion.
- [x] 2.3 Run a manual restore of a file of a few hundred megabytes (a throwaway target) and verify it completes with the same progress display and no noticeable slowdown.

## 3. Verification

- [x] 3.1 Run `dotnet build Vara.slnx` (no warnings) and `dotnet test Vara.slnx`, and verify everything passes.
- [x] 3.2 Stop and leave the working tree uncommitted for review. Do not archive or sync the change until the author has reviewed and committed the implementation.
