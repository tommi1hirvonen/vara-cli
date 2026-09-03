## Why

The prune command currently gives no feedback while it runs. Most of its work (listing snapshots, evaluating retention, the bulk snapshot deletion) is fast enough not to need it, but the final garbage-collection step - deleting every unreferenced blob from the content store one at a time - can take a perceptible amount of time on a store with many blobs, with nothing shown to the user in the meantime.

## What Changes

- Add an indeterminate spinner while prune performs its DB/diff steps (listing snapshots, evaluating retention, computing unreferenced blobs), whose duration isn't practically predictable upfront.
- Once the set of unreferenced blobs to delete is known, replace the spinner with a count-based progress indicator ("Deleting blobs x / y") for the blob-deletion loop, since each deletion is a discrete, similarly-cheap unit of work rather than a byte transfer.
- Non-interactive/redirected output falls back to plain output, consistent with the backup and restore commands' existing fallback behavior.

## Capabilities

### Modified Capabilities
- `progress-reporting`: extends progress reporting to the prune command's blob-deletion phase, using a count-based (not byte-based) indicator - a new requirement distinct from backup/restore's byte-based progress.
- `retention-pruning`: the prune operation's behavior gains a progress-reporting expectation for its blob-deletion phase.

## Impact

- `Vara.Application/Retention/PruneService.cs`: `Prune` accepts a progress callback (or callbacks) reporting the spinner phase's activity and the blob-deletion loop's count/total.
- `Vara.Cli/Commands/PruneCommand.cs`: wires up a live spinner + count-based progress bar (interactive) or plain output (non-interactive).
- Test files for each of the above.
