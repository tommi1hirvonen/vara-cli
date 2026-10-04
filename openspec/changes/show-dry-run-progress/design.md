## Context

See `proposal.md` for the motivation and `specs/progress-reporting/spec.md` for the behavior contract. The real backup's interactive path already renders an indeterminate scan phase through the unified backup progress display. The dry-run path calls `PlanOnly` synchronously without a progress callback or display. JSON mode already bypasses the human-readable progress display, while non-terminal output uses the established plain-output path.

## Goals / Non-Goals

**Goals:**
- Reuse the existing scan-phase visual treatment during interactive dry-run planning.
- Keep JSON and redirected-output behavior consistent with their existing contracts.
- Keep the change confined to CLI presentation; planning semantics remain unchanged.

**Non-Goals:**
- Add percentage or time estimates for dry-run scanning.
- Add scan progress callbacks to the application layer.
- Change the dry-run summary, scan behavior, or JSON schema.

## Decisions

- **Run interactive dry-run planning inside the existing live progress lifecycle.** When human-readable output is selected and `OutputMode.IsLiveCapable` is true, start the existing backup progress display with its scan task marked indeterminate, execute `PlanOnly` while the display is active, then let the display stop before reporting the summary. This provides ongoing feedback without requiring a fabricated percentage or changes to the scanner/pipeline interfaces.
- **Use the existing scan-phase bar rather than introduce a separate spinner.** The current unified progress column already renders the scan phase as a full-width indeterminate bar; reusing it keeps real and dry-run scanning visually consistent. A standalone spinner would introduce a second presentation and would not align with the current scan layout.
- **Leave non-live and JSON paths outside the progress lifecycle.** JSON continues to call `PlanOnly` directly and emit only its result object. When live progress is unavailable, preserve the existing non-interactive behavior rather than printing animation-like output into redirected streams.
- **Keep plan errors under existing error reporting.** Exceptions from planning should unwind the progress display and continue through the command's current error handling; the indicator must not turn a failed plan into a success-shaped result.

## Risks / Trade-offs

- [The scan may finish before the terminal refreshes the display] → Use the progress system's existing automatic refresh; do not add artificial delay to force a visible animation.
- [Progress rendering or an error could interfere with the dry-run summary] → End the progress lifecycle before calling the existing summary reporter, and cover the order and JSON suppression in CLI tests.

## Migration Plan

No migration is required. The change affects only transient CLI output and introduces no persisted data or configuration.
