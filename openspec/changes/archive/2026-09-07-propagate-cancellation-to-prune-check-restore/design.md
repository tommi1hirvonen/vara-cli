## Context

`Program.cs` builds one `GracefulCancellation` instance and wires `Console.CancelKeyPress` to
its `RequestCancellation()`, but passes `gracefulCancellation.TokenSource.Token` only into
`BackupCommand.Create(...)`. `PruneCommand`, `CheckCommand`, and `RestoreCommand` (whose
`--recursive` path runs its own loop over planned writes/removals) are constructed without a
token at all, so `RequestCancellation()` firing during any of them has no observer. See
proposal.md - Why.

`GracefulCancellation` already implements the "first Ctrl+C requests a cooperative stop, second
Ctrl+C hard-exits via `Environment.Exit`" behavior and is unit-tested independently of
`BackupCommand`; this design reuses it as-is rather than introducing a second cancellation
primitive.

## Goals / Non-Goals

**Goals:**
- `prune`, `check`, and `restore --recursive` observe the same `GracefulCancellation` token
  `BackupCommand` already does, stopping cleanly on a first Ctrl+C and hard-exiting on a second,
  with no change to `GracefulCancellation` itself.
- Keep the mechanism coarse-grained and simple: check the token between the top-level units of
  work each command already iterates one at a time, not mid-unit.

**Non-Goals:**
- No new checkpoint/resume machinery, and no partial-unit-of-work cancellation (e.g. cancelling
  mid-way through copying a single blob or a single file's write). Per the user's direction, a
  simpler, coarser check between whole units is sufficient here - unlike backup, which already
  has bespoke checkpointing.
- No change to `BackupCommand`'s existing cancellation handling.
- No change to `GracefulCancellation`'s own semantics (single-stop / second-forces-exit).

## Decisions

**Thread the token as a plain `CancellationToken` parameter, mirroring `BackupCommand`.**
`BackupCommand.Create` already accepts `CancellationToken cancellationToken = default`; give
`PruneCommand.Create`, `CheckCommand.Create`, and `RestoreCommand.Create` the same parameter, and
update `Program.cs` to pass `gracefulCancellation.TokenSource.Token` to all four. This keeps every
command's signature and wiring consistent rather than introducing a different mechanism for some
commands.

**Push the token down to the layer that owns the iteration loop, not just the CLI command.**
Each command's `Create` method largely delegates to an application-layer service (the retention
pipeline for prune, the verification pass for check, `SnapshotHistoryService`'s directory-restore
executor for recursive restore). The token needs to reach whichever method actually loops over
snapshots/blobs/paths, so it can check `cancellationToken.ThrowIfCancellationRequested()` (or an
equivalent check-and-stop) between iterations. This is a plain parameter threaded through the
existing call chain - no new abstraction.

**Cancellation is reported as a successful, cancelled outcome, not an error.** This mirrors
backup's existing distinction between a `Cancelled` snapshot status and a `Failed` one, and
prune's existing "user declined the confirmation prompt" scenario, which already exits
successfully while reporting cancellation rather than raising an error. Each affected service
checks `cancellationToken.IsCancellationRequested` between units of work and stops by returning
early with a `Cancelled` flag set on its result type - the same mechanism `BackupExecutor`/
`BackupPipeline` already use - rather than throwing `OperationCanceledException`. Reusing this
established in-codebase pattern (rather than introducing exception-based cancellation alongside
it) keeps the two mechanisms consistent; each command's action reads the returned `Cancelled`
flag and renders the same cancelled-not-error outcome instead of letting anything propagate to
`ErrorReporting`'s catch-all.

**Prune's cancellation granularity is coarser than a literal per-snapshot check.** Retention
evaluation and snapshot removal (`repository.PruneSnapshots`) run as one atomic SQL transaction
today, not a loop over individual snapshots - restructuring that into a per-snapshot loop so
cancellation could take effect mid-removal would mean splitting one atomic transaction into many,
a bigger and riskier change than this simplified-cancellation change intends. Per the user's
explicit choice, `PruneService.Prune` instead checks the token once before that atomic step
starts (skipping it entirely - removing no snapshots - if already cancelled) and then per-blob
during the genuinely iterative garbage-collection loop, where a check between iterations fits
naturally. The `retention-pruning` spec delta's requirement text and scenarios reflect this
atomic-step-boundary granularity rather than a literal "between each snapshot" check.

**Where each command checks the token:**
- `prune`: once before retention evaluation and snapshot removal start (that pair runs as one
  atomic step - `repository.PruneSnapshots` is a single transaction - so a cancellation requested
  before it starts skips it entirely rather than partially applying it), and between each blob
  garbage-collected.
- `check`: between each referenced blob verified.
- `restore --recursive`: between each planned path written or removed (the directory-restore
  executor already iterates a precomputed `DirectoryRestorePlan`, so this is a check at the top
  of that existing loop).

## Risks / Trade-offs

[Coarse-grained checks mean a single very slow unit of work - e.g. hashing one very large blob
during `check` - still delays a graceful stop until that unit finishes] → Accepted per the user's
"simpler approach is sufficient" direction; matches the granularity already implied by each
command's existing progress reporting (per-blob, per-path), so no new instrumentation is needed
to find a check point.

[Threading a token through several application-layer methods touches more call sites than
`Program.cs` alone] → Each touched method already receives similar optional/callback parameters
(e.g. `onBytesCopied`, `onSizeResolved` in `SnapshotHistoryService`), so adding a
`CancellationToken` parameter follows an established pattern in this codebase rather than a new
one.
