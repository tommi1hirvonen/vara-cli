## Context

See `proposal.md` for motivation. Today's implementation (`BackupProgressColumn.RenderBarRow`) builds a two-column `Grid`: `[bar][trailing text]`, where `trailing text` is the scan phase's `" Scanning files..."` label or the transfer phase's `" {percent}%"` text, and the bar's width is `availableWidth - trailingText.Length`. Because the scan label and the percent text differ in character count, the bar itself renders at a different length in each phase today, in addition to having no left-side label at all.

## Goals / Non-Goals

**Goals:**
- Phase description renders to the left of the bar for both scan and transfer phases.
- Transfer phase gains a phase description where none exists today.
- Percent text remains to the right of the bar during transfer.
- The bar renders at the same length in both phases - not just by adding a same-width placeholder on the right during scan, but by fixing *both* the label column and the trailing column to constant widths shared across phases, since the label text itself ("Scanning files..." vs "Backing up...") also differs in length and would otherwise still shift the bar's width between phases even with a right-side placeholder alone.

**Non-Goals:**
- No change to `BackupProgressCalculator`, `ProgressDisplayGate`, or the stats line's layout.
- No change to the scan/transfer/outcome coloring logic - only the row's column layout and text content change.
- No change to the underlying `BackupProgressRole`/`BackupProgressOutcome` state model.

## Decisions

### Both the label column and the trailing column use fixed widths, sized to the longer of the two phases' content
`RenderBarRow` currently sizes only the bar and the trailing column; a new label column is added to its left. To guarantee the bar itself is the same width in both phases (the actual goal, not merely "add a placeholder"), both the label column and the trailing column must be fixed to the same width regardless of which phase is rendering:
- Label column width = the longer of `" Scanning files..."` and `" Backing up..."` (left-padded/right-padded consistently, e.g. right-aligned or left-aligned within that fixed width - pick left-aligned with trailing space fill, matching how the rest of the row reads left-to-right).
- Trailing column width = the width already reserved for the percent text (e.g. `" 100.0%"`'s width) - unchanged from today's sizing, just now also used to size the scan phase's placeholder.

This is a slightly bigger change than "add a placeholder on the right" alone, but is necessary to actually satisfy the "equally long bar in both phases" requirement - a right-side placeholder alone would still leave the bar's width dependent on the label text's length, since the label column would otherwise only be as wide as whichever label happens to be rendering.

Alternatives considered: sizing the label column per-phase (as wide as that phase's own label, no more) and only fixing the trailing column - rejected, this is exactly today's asymmetric approach applied to a new column, and reintroduces the same bar-width inconsistency the change is meant to fix, just shifted from the right side to the left side.

### Scan-phase placeholder is blank space, not synthetic content
The trailing column's scan-phase placeholder is rendered as blank spaces filling its fixed width, rather than synthetic text resembling a percentage (e.g. `"--.-%"`).

Alternatives considered: rendering something like `" --.-%"` - rejected, this risks being misread as a stalled or broken percentage display (as if progress should be numeric but isn't updating), rather than clearly communicating "not applicable in this phase."

### Transfer phase description text: `"Backing up..."`
Chosen for two reasons: it matches the command's own name (`vara backup`), and it is grammatically symmetric with the existing scan label `"Scanning files..."` (both are present-participle phrases describing the phase underway).

Alternatives considered: `"Copying..."` - more literally describes the byte-level operation (and also describes the mirror-placement fallback copy pass), but was set aside in favor of the more command-symmetric, less implementation-specific `"Backing up..."`. This is a low-cost decision to revisit if preferred during implementation review.

### Column ordering achieved via the same `Grid` technique already in use
`RenderBarRow` already uses a `Grid` (not `Columns`) to place the bar and trailing text side by side on one line, per the prior `polish-backup-progress-bar` change's design.md finding that `Columns` pushes a "greedy" full-width child (the bar) and a second child onto separate rows rather than the same line. Adding a third, leading column (the label) to the same `Grid` uses the identical mechanism - no new layout technique is introduced.

## Risks / Trade-offs

- [Risk] Fixing both side columns to worst-case widths reserves more total horizontal space for text than today's transfer-phase row alone needed (which only reserved space for the percent), narrowing the bar somewhat on narrow terminals → Mitigation: the amounts involved are small (a couple dozen characters at most across both fixed columns), and the existing "terminal width cannot be determined -> plain fallback" behavior already protects the case where no reasonable bar width can be computed at all.
- [Trade-off] Blank-space placeholder means the scan phase's row has an empty-looking area where transfer's percentage will later appear, rather than any content filling it → acceptable, this is the intent (nothing meaningful to show yet), and avoids the misleading-percentage risk noted above.
