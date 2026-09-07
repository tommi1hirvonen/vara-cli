## Context

See proposal.md - Why. `BackupPipeline.Run` (`src/Vara.Application/Backup/BackupPipeline.cs`) acquires the run lock, then unconditionally: reconciles incomplete snapshots, probes hardlink support, cleans up orphaned temp files, begins a new snapshot row, scans/diffs/plans, executes the plan, and commits. Several of those steps mutate the target (temp cleanup deletes files, hardlink probing creates/removes a throwaway link, `BeginSnapshot` writes a DB row immediately) even before any real transfer happens. `BackupCommand` (`src/Vara.Cli/Commands/BackupCommand.cs`) picks between a live (interactive) and plain (non-interactive) progress display based on `OutputMode.IsLiveCapable`, then reports via `BackupOutcomeReporter`/`BackupRunSummaryFormatter`.

## Goals / Non-Goals

**Goals:**
- A `--dry-run` run performs zero writes of any kind - not just "no manifest commit," but no mutation to the target at all.
- `--dry-run`'s reported counts come from the same scan/diff/move-detection logic a real run uses, so they're trustworthy previews, not an approximation.
- `--json` produces one parseable JSON object on stdout and nothing else, for both dry-run and executed outcomes, sharing one schema shape.

**Non-Goals:**
- Not making `--json`/`--dry-run` a CLI-wide convention now (confirmed scope: `backup` only). Other commands keep their existing output.
- Not changing what a real (non-dry-run) run without `--json` does or looks like.
- Not adding progress reporting *during* a dry run's scan phase beyond what already exists (an indeterminate "Scanning" indicator); a dry run has no transfer phase to show a byte-progress bar for.

## Decisions

**`BackupPipeline` gets a new `PlanOnly(Profile profile)` method, sibling to `Run`, sharing its constructor-injected dependencies.** It acquires the run lock (per the confirmed `dry_run_lock_behavior: acquire-lock` decision - a dry run still refuses to start under the same "target busy" condition a real run would, and its own planning reflects a consistent view of the manifest not concurrently mutated by another run) but explicitly skips `ReconcileIncompleteSnapshots`, `ProbeHardlinkSupport`, `CleanupOrphanedTemp`, `BeginSnapshot`, and `BeginManifestBatch` - none of which are needed to compute a plan, and several of which mutate the target. It calls `repository.GetCurrentState()`, `scanner.Scan(profile.Sources)`, `new BackupDiffer().Diff(...)`, and `new BackupPlanner(...).Plan(...)` - the exact same stages `Run` already uses - and returns a new `BackupPlanSummary` record (counts per `PlannedOperationKind`, `TotalBytesToTransfer` from the plan, and scan failure paths) without touching `BackupExecutor` or the repository's snapshot-writing methods at all.

**One JSON schema shared by dry-run and executed outcomes, discriminated by a `mode` field**, rather than two unrelated JSON shapes. Rejected alternative: two independent schemas (one for `BackupRunResult`, one for `BackupPlanSummary`) - considered simpler at first, but it forces every consumer/script to branch on which shape it received rather than checking one `mode` field, and the two outcomes already share most of their meaningful content (added/changed/moved/deleted counts, bytes, failure paths). The shared shape:
```json
{
  "mode": "dry-run" | "executed",
  "profile": "<profile name>",
  "startedAt": "<ISO 8601>",
  "completedAt": "<ISO 8601>",
  "cancelled": false,
  "added": 0, "changed": 0, "moved": 0, "deleted": 0,
  "failed": 0,
  "failedPaths": ["..."],
  "bytesTransferred": 0
}
```
For `mode: "dry-run"`: `added`/`changed`/`moved`/`deleted` are the *planned* counts, `bytesTransferred` is the *planned* total bytes to transfer, `failed`/`failedPaths` reflect scan failures only (nothing has executed yet to fail), and `cancelled` is always `false` (a dry run cannot be gracefully cancelled mid-transfer, since there is no transfer). For `mode: "executed"`: every field carries its existing real-run meaning (`BackupRunResult`'s current fields), unchanged from today.

**`--json` suppresses progress display entirely (no live or plain progress rendering)**, printing only the final JSON object to stdout once the run/plan completes. Rejected alternative: still show human progress on stderr while emitting JSON on stdout - rejected as unnecessary complexity for this proposal's scope; a script invoking `--json` almost always redirects or captures stdout only and doesn't need a progress stream, and keeping the behavior simple (`--json` means "only JSON, nothing else, on stdout") is easier to document and reason about than a dual-stream contract.

**Exit codes are unchanged by `--json`/`--dry-run` individually**: a dry run reporting scan failures still maps to `ExitCodes.PartialFailure`, exactly as a real run with failed paths does today (per `BackupCommand`'s existing "outcome severity determines exit code" logic) - `cancelled` is structurally always `false` for a dry run (see above), so that branch of the existing exit-code logic simply never triggers for `mode: "dry-run"`.

## Risks / Trade-offs

- [`PlanOnly` duplicates `Run`'s scan/diff/plan invocation sequence rather than `Run` calling into a shared internal helper] → Extract the shared scan→diff→plan sequence into a small private helper both `Run` and `PlanOnly` call, so the two entry points can't silently drift apart on which stages they invoke in which order.
- [A dry run's plan can go stale immediately if a concurrent process changes source files after planning but before a user reads the report] → Inherent to any "preview" operation - documented as advisory ("what would happen right now"), not a guarantee about a future real run; the run lock still prevents it from racing another *Vara* run against the same target, which is the concurrency guarantee this proposal is scoped to.
- [Adding a `mode` discriminator field to the JSON schema instead of shipping dry-run/executed as separate top-level shapes] → Documented above in Decisions; the trade-off is a single consumer contract at the cost of a few fields having mode-dependent meaning, judged worthwhile for scripting simplicity.
