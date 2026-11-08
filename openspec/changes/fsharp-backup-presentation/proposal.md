## Why

After `fsharp-backup-engine`, `vara backup` runs the F# engine but prints only an interim plain-text summary. This change gives the F# backup command the output users get from the C# build:
- the live progress display;
- the run summary and its outcome styling;
- the dry-run display;
- JSON output;
- the Windows Terminal progress indicator;
- sleep prevention during a run.

After this change, F# `vara backup` is complete. That makes it the place for the series' second decision point, **Gate B**: the AOT-compiled F# backup measured against the C# one on a quiet machine.

Design and tasks for this change are written by `fsharp-backup-engine`'s final task, using its findings.

## What Changes

- Add live backup progress to `Vara.Cli`, as a projection of the engine's run state:
  - the scan and transfer phases;
  - byte, count, throughput and ETA fields that keep a fixed width;
  - the active-transfer list;
  - rate-limited, never-regressing updates;
  - a heartbeat during stalls;
  - the outcome-styled final bar.
- Add the run summary, the dry-run display and `--json` output for real and dry runs.
- Add the shared presentation conventions:
  - outcome styles and exit codes;
  - plain-text fallback for redirected output, `NO_COLOR` and terminals without color support;
  - hard-error rendering;
  - the Windows Terminal progress indicator.

  Later changes reuse them.
- Add sleep prevention through the Win32 power-request API. A failure to create or clear the request never blocks the backup.
- As its last task, update the following changes with this change's findings.

There are no user-visible changes: the shipped `vara.exe` is still the C# build.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`).

**Owned scenarios:**
- **`progress-reporting`:** every requirement except "Count-based progress indication for non-byte-transfer operations" (owned by `fsharp-retention-and-integrity`), and "Aggregate progress across a multi-file directory restore" and "Restore task labels are bounded to a fixed width" (owned by `fsharp-snapshot-history`).
- **`cli-presentation`:** "Outcome severity is visually distinct", "Outcome severity determines process exit code", "Non-interactive or color-incapable output falls back to plain text", "Unclassified exceptions are rendered in the hard-error style" and "Machine-readable JSON output for the backup command".
- **`backup-execution`:** "Automatic system sleep is prevented during a real backup" and "Sleep-prevention request failure does not block backup".

## Impact

- **Code:** `src/fsharp/Vara.Cli` gains the presentation code; `src/fsharp/Vara.Engine` gains power-request interop. `src/fsharp/Vara.Domain` may gain pure progress calculations (throughput, ETA, rate limiting). No changes to the C# projects.
- **Tests:** presentation tests use `Spectre.Console.Testing`. A parity check compares the C# and F# JSON output of the same run.
- **Gate B:** both `vara.exe` builds are published with Native AOT and measured on a quiet machine:
  - wall-clock time for a first backup, a "nothing changed" incremental backup and a large tree (100k+ files) must be within ±5% of C#;
  - peak memory and executable size are recorded and must be within a margin agreed when the design is written;
  - there must be zero AOT warnings.

  If Gate B fails, the series stops as described in `fsharp-backup-engine`'s migration roadmap.
- **Dependencies:** requires `fsharp-backup-engine`.
