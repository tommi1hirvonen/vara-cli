## Context

See proposal.md - Why. The pieces this command needs largely already exist:
- `ISnapshotRepository.GetAllReferencedContentHashes()` - every content hash still referenced by at least one file-version row, across all snapshots (already used by prune's GC step).
- `IContentStore.ListAllStoredHashes()` - every blob hash physically present in the store (already used by prune's GC step, paired with the above, to compute safe-to-delete blobs).
- `IContentStore.OpenRead(hash)` / `HasContent(hash)` - read a blob's content or check its presence.
- `IHasher.ComputeHash(Stream)` - the same (non-cryptographic, XxHash128) hash function already used to compute a blob's identity when it was first stored.

What's missing is (a) a way to map a problem hash back to an actionable file path for reporting, and (b) the command/service wiring.

## Goals / Non-Goals

**Goals:**
- Detect target-side data loss (missing blobs) and target-side corruption (content no longer matching its recorded hash) across the full referenced history.
- Make findings actionable: report at least one affected file path per problem hash, not just an opaque hash value.
- Keep the check strictly read-only.
- Reuse existing abstractions (`IContentStore`, `ISnapshotRepository`, `IHasher`) rather than introducing a parallel verification mechanism.

**Non-Goals:**
- Not detecting source-side drift (a source file changing without its mtime/size changing) - see proposal.md. `vara check` never touches the source, only the target.
- Not a cryptographic integrity/tamper-detection guarantee. `IHasher`'s own doc comment is explicit that it is "not a security/integrity guarantee" (XxHash128, non-cryptographic) - this check catches accidental bit rot, truncation, and out-of-band corruption of the target, not deliberate tampering by an adversary with write access to the target.
- Not auto-remediating anything found. Orphaned blobs are reported but not deleted (that remains `vara prune`'s job); missing/corrupt blobs have no automatic fix (the only real fix is re-running a backup from source, which the report's affected-path output points a user toward, but `vara check` itself does not trigger a backup).
- Not adding a `--json` output mode for this command now. `--json` for scripting was scoped, in a separate proposal, to the `backup` command only; `vara check` already provides a scriptable non-zero exit status per its own requirement, which covers the primary "usable in CI/automation" need without expanding this proposal's surface.

## Decisions

**Verify the full referenced history, not just the current live mirror** (confirmed direction). `GetAllReferencedContentHashes()` already returns hashes across every snapshot, including ones only reachable via historical (non-current) file versions - using it directly, rather than filtering down to `GetCurrentState()`'s hashes, means `vara check` actually answers "can every version I could restore still be restored," which is the more complete and more useful guarantee, and requires no new repository surface for the referenced-hash side.

**Full re-hash by default, `--quick` opt-in for presence-only** (confirmed direction). A missing blob is unambiguous data loss and cheap to detect either way; a corrupt-but-present blob (bit rot, partial write, out-of-band edit) is only caught by actually reading and re-hashing its content, which is the more thorough and safer default for a tool whose entire job is safeguarding data - a user who wants to trade thoroughness for speed on a very large store opts into that explicitly via `--quick`, rather than silently getting the weaker check by default.

**Add `ISnapshotRepository.GetPathsForContentHash(string hash)` returning the distinct relative paths that reference a given content hash.** Considered alternative: report only the opaque hash value for missing/corrupt blobs, leaving path lookup to a separate, manual step (e.g. `vara history` filtered by hash, if that existed). Rejected because an unresolved hash is not actionable for a user deciding what to re-back-up or investigate; the underlying SQL is a straightforward indexed lookup (`file_versions` already has `idx_file_versions_content_hash`), so the cost of adding this is low relative to the reporting value. The new method returns every distinct `relative_path` with a `file_versions` row for that hash (not filtered to only-current rows), since a historical-only reference is still worth surfacing - a user restoring an old version of a since-deleted file still needs to know that version's blob is missing/corrupt.

**New `Vara.Application.Integrity.IntegrityCheckService`, mirroring `PruneService`'s shape** (constructed from `ISnapshotRepository`/`IContentStore`, exposing a method that accepts a `quick` flag and an `IProgress<T>` callback, returning a result record with missing/corrupt/orphaned counts and affected-path detail) - consistent with the existing `PruneService`/`BackupPipeline` pattern of one focused application-layer service per CLI command, keeping `CheckCommand` itself a thin presentation layer like `PruneCommand`.

**Progress reporting**: reuse the existing `progress-reporting` capability's count-based indicator (blobs checked so far / total referenced blobs) for the potentially-long full re-hash pass, the same pattern `vara prune`'s blob-deletion phase already uses - no new progress-reporting capability requirement needed, since the existing one already covers a count-based operation over a known total.

**Exit code**: reuse the existing `ExitCodes.PartialFailure` (2) for "problems found" rather than minting a new exit code, since it already carries exactly this meaning elsewhere ("the run completed but not everything succeeded," distinct from `ExitCodes.HardError` for an unrelated failure like a missing profile) and CLI-facing scripts already have a reason to distinguish it from success/hard-error.

## Risks / Trade-offs

- [Full re-hash mode reads every byte of every referenced blob - potentially the entire logical size of a profile's history - which can be slow and I/O-heavy for a large backup] → `--quick` is available as an explicit, faster alternative; the default favors correctness over speed since this command's entire purpose is a thoroughness guarantee, and it is expected to be run occasionally (e.g. periodically or before relying on a restore), not on every backup.
- [`GetPathsForContentHash` returning every distinct historical path for a hash could be a long list for heavily-deduplicated content shared across many files/versions] → Reporting caps or truncates the listed paths per problem hash with a "+N more" summary when the list is large, keeping output readable without losing the count.
- [A blob shared by many files/versions (deduplication) means one missing/corrupt blob can surface as many affected paths at once] → This is accurate and desired: it reflects the real blast radius of that one piece of lost/corrupted content, which is exactly the information a user needs to judge severity.
