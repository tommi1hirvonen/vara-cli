## Why

`vara prune` permanently deletes snapshot records and their now-unreferenced content with no
confirmation of any kind - a single mistyped profile name or a policy that's stricter than the
user expected results in irreversible data loss with no chance to back out. `vara restore`
already guards its own destructive action (overwriting an existing file) with an interactive
confirmation prompt and a non-interactive override; prune should get the same protection,
adapted to the fact that its risk (how many snapshots, discovered in advance) differs from
restore's (whether a file exists, discovered on attempt).

## What Changes

- Prune now previews the snapshots eligible for removal before deleting anything, by reusing the
  existing (already pre-computed) eligibility evaluation as a read-only preview step.
- WHEN at least one snapshot is eligible for removal AND the command is running with an
  interactive input stream AND no explicit override was given, the system prompts the user to
  confirm before proceeding, defaulting to not pruning if the user provides no explicit answer -
  mirroring restore's interactive-prompt pattern.
- WHEN at least one snapshot is eligible for removal AND the command is running with a
  non-interactive input stream AND no explicit override was given, the system reports a clear
  error explaining that an explicit override is required, and performs no deletions -
  mirroring restore's non-interactive-rejection pattern.
- Adds a new `--yes`/`-y` option to `vara prune` that explicitly authorizes the run, skipping the
  confirmation prompt regardless of interactivity. Deliberately a distinct name from restore's
  `--force`, since it confirms a destructive count-based action rather than an overwrite.
- WHEN zero snapshots are eligible for removal, the system skips the prompt entirely and proceeds
  as today (still running unreferenced-content garbage collection and reporting the outcome) -
  mirroring restore's "destination doesn't exist, no prompt needed" scenario.
- WHEN the user declines the interactive prompt (or provides no answer), the system leaves all
  snapshot records and content untouched, reports that the prune was cancelled, and exits
  successfully rather than reporting an error.
- The confirmation prompt states how many snapshots will be permanently removed and notes that
  content no longer referenced by any remaining snapshot will also be freed, without stating an
  exact blob count (that count is only known after deletion completes).

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `retention-pruning`: the "Prune command" requirement gains a confirmation gate - previewing
  eligible snapshots, prompting interactively (with a decline/cancel path), rejecting
  non-interactive runs without an explicit override, and adding a `--yes`/`-y` override option.

## Impact

- `Vara.Cli.Commands.PruneCommand`: adds the `--yes`/`-y` option, the interactive/non-interactive
  branching, and the cancelled-run reporting path (mirroring `RestoreCommand`'s structure).
- `Vara.Application.Retention.PruneService`: exposes the eligibility evaluation as a read-only
  preview usable before the destructive `Prune` call, without changing `Prune`'s existing
  behavior or its run-lock semantics.
- `Vara.Cli.Presentation`: a new neutral-style "prune cancelled" message, matching the existing
  neutral-style "restore cancelled" message.
- No changes to `Vara.Core` or `Vara.Infrastructure` - the confirmation is a CLI/Application-layer
  concern layered on top of the existing eligibility evaluation and deletion logic.
