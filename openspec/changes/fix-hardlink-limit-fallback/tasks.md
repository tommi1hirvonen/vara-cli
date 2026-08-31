## 1. Content store fallback

- [ ] 1.1 Add an internal testing seam to `FileSystemContentStore` that lets a test force a
      single `PlaceAtMirrorPath` call's hardlink attempt to fail (mirroring the existing
      `ForceHardlinkSupportForTesting` pattern), and verify it compiles and is usable from
      `Vara.Infrastructure.Tests`.
- [ ] 1.2 Wrap the `Kernel32.CreateHardLink` call inside `PlaceAtMirrorPath` in a try/catch for
      `IOException`, falling back to `File.Copy(blobPath, stagingPath)` on failure, and verify
      the existing hardlink-success and volume-level-copy-fallback tests still pass unchanged.
- [ ] 1.3 Update `IContentStore.PlaceAtMirrorPath`'s doc comment to describe the per-placement
      fallback in addition to the existing per-volume fallback, and verify the build succeeds
      with no other call sites needing changes.

## 2. Tests

- [ ] 2.1 Add a `FileSystemContentStoreTests` case that forces a single placement's hardlink
      attempt to fail (via the new seam) and verifies the mirror path ends up with a real copy
      of the correct content rather than the call throwing.
- [ ] 2.2 Add a `FileSystemContentStoreTests` case verifying that after a forced-failure
      placement falls back to copy, a subsequent placement for the same or a different mirror
      path (with the seam no longer forcing failure) again attempts and succeeds via hardlink,
      confirming no per-hash state is cached across calls.
- [ ] 2.3 Add a `BackupExecutorTests` case (using the existing fake content store) confirming
      that when `PlaceAtMirrorPath` succeeds via fallback rather than throwing, the operation is
      recorded as Added/Changed with a manifest entry written, and is not counted as failed.

## 3. Verification

- [ ] 3.1 Run `dotnet test` for `Vara.Infrastructure.Tests` and `Vara.Application.Tests` and
      verify all tests pass, including the new cases from section 2.
