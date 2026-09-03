## Why

During a backup run, the phase description (e.g. the scan phase's label) is rendered to the right of the progress bar, and the transfer phase has no phase description at all - only a percentage. This leaves the user without a clear "what's happening right now" cue once transfer begins, and the two phases' bars render at different lengths (the scan bar's trailing label is a different width than the transfer bar's trailing percentage), which looks inconsistent.

## What Changes

- Move the phase description to the left of the progress bar for both the scan and transfer phases, instead of rendering it as trailing text on the right.
- Add a phase description for the transfer phase (e.g. "Backing up...") where none exists today.
- Keep the percentage on the right of the bar during the transfer phase, since it remains useful there.
- Add a placeholder on the right of the bar during the scan phase (which has no percentage, being indeterminate) so the scan phase's bar renders the same length as the transfer phase's bar.

## Capabilities

### Modified Capabilities
- `progress-reporting`: the "Progress bar occupies a dedicated, width-sized line" requirement's layout changes - phase description moves from trailing (right of bar) to leading (left of bar), the transfer phase gains a phase description, and the scan phase gains a right-side placeholder so both phases' bars render the same length.

## Impact

- `Vara.Cli/Presentation/BackupProgressColumn.cs`: `RenderBarRow`'s grid layout (label/placeholder position, column ordering), `RenderScan`'s and `RenderBar`'s trailing-text arguments.
- `Vara.Cli.Tests/Presentation/BackupProgressColumnTests.cs`: layout assertions.
- No changes to `BackupProgressCalculator`, `ProgressDisplayGate`, or the stats line.
