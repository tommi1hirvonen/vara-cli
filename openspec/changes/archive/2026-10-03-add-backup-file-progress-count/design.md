## Context

See proposal.md for the motivation and specs/progress-reporting/spec.md for the user-visible behavior. The backup plan already distinguishes Add/Change content transfers from metadata-only operations, and the CLI currently receives byte progress incrementally while rendering a full-width bar above a fixed-width statistics row.

## Goals / Non-Goals

**Goals:**
- Carry the planned and successfully completed content-file counts through backup progress reporting.
- Keep file completion progress independent from chunk-level byte progress and its throughput/ETA calculations.
- Present interactive statistics in two stable rows and include the file count in plain-text progress.

**Non-Goals:**
- Changing the byte-based progress percentage, throughput, or ETA calculations.
- Counting moves, links, deletions, scan failures, or failed content transfers as completed content transfers.
- Changing restore progress, final run summaries, persisted snapshot statistics, or command-line options.

## Decisions

- **Use planned Add/Change operations as the file denominator.** These are exactly the operations whose content is transferred. Derive the total from the same plan used for the byte denominator; do not count the plan's metadata-only operations.
- **Advance the file numerator once per successful content operation.** Report a file as complete only after its transfer and manifest recording succeed. Keep chunk callbacks responsible for byte progress, so a failed operation can leave byte progress advanced without falsely appearing as a successfully completed file.
- **Extend the live progress value with file counts.** The pipeline supplies initial zero progress and updates both byte and file counts as execution proceeds. File-count updates must remain correct under concurrent transfer workers and must not affect the calculator's byte-based timing.
- **Use two fixed-width statistics rows for interactive output.** Put bytes transferred/total and files transferred/total on the first row; put throughput and ETA on the second. Retain the existing dedicated full-width bar above the statistics and fixed field widths within each row to prevent shifting.
- **Keep plain-text progress as appended output with the new metric included.** This preserves its existing non-interactive behavior while keeping reported progress consistent with the interactive display.

## Risks / Trade-offs

- [A file may contribute byte progress before it later fails validation or manifest recording] -> Keep bytes as the existing independent transfer measure, and increment the completed-file count only after the full operation succeeds.
- [Two statistics rows consume more vertical terminal space] -> Keep the bar on its own line and use two compact, fixed-width rows rather than crowding four values into one row.
- [Concurrent file completions can race] -> Update the completed-file count atomically and report a consistent snapshot of both progress dimensions.

## Migration Plan

No persisted data or command-line interface migration is required. Deploy the updated CLI normally; rollback consists of reverting the progress display and progress-event changes.
