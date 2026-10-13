## Context

See `proposal.md` for motivation and `specs/progress-reporting/spec.md` for observable requirements. Backup, restore, integrity check, and prune already calculate progress in the CLI presentation layer; their live displays also have indeterminate phases where work totals are not yet known. The feature must not affect redirected or JSON output.

## Goals / Non-Goals

**Goals:**

- Reuse the progress values and phase changes already produced by each command.
- Keep terminal-host integration optional, isolated, and testable without a real Windows taskbar.
- Ensure progress updates and teardown are ordered so a late callback cannot restore stale taskbar state after a command ends.

**Non-Goals:**

- Add progress reporting to commands that do not already report it.
- Change progress calculations, terminal display layouts, command output, or exit codes.
- Control taskbar indicators outside a terminal host that supports the Windows Terminal progress protocol.

## Decisions

### Use Windows Terminal OSC 9;4 sequences

Publish indeterminate, determinate, and clear states with the OSC 9;4 sequences supported by Windows Terminal. This protocol lets the terminal own its tab and taskbar presentation and requires no window handle or direct Shell COM integration.

Alternatives considered:

- Calling `ITaskbarList3` directly would require locating and managing a suitable window handle, which a CLI hosted in Windows Terminal does not own.
- A native Windows-only API would not cover the terminal-hosted UI that users requested and would add platform interop complexity.

### Add a shared CLI presentation publisher

Use a small shared publisher in `Vara.Cli` for sequence formatting, host/output eligibility, and state emission. Feed it the existing progress snapshots or counts from each command; do not duplicate progress calculations or move terminal concerns into `Vara.Application`.

The publisher should have an injectable output seam so tests can assert the emitted sequences and verify that ineligible sessions emit nothing.

### Gate output to interactive Windows Terminal sessions

Emit sequences only when the process is in Windows Terminal, standard output is a terminal, and that command is producing human-readable progress. Use the existing live-output decision for terminal status and a Windows Terminal host signal for host identification. Keep JSON branches and redirected output outside the publisher.

This deliberately does not attempt to detect every terminal that might optionally support OSC 9;4. That keeps behavior unchanged for other hosts as required by the spec.

### Mirror existing phases and completion values

Send indeterminate state when an existing progress path begins an unknown-work phase, then send determinate updates from its existing percentage or count-based progress. For backup, include the scan phase and the dry-run scan; for check and prune, include their existing initial evaluation phases. Restore becomes determinate when its existing size-resolved progress begins. Clamp emitted percentages to 0-100 without changing the value shown by the existing display.

Clear the indicator after success, cancellation, or failure. Serialize progress writes and make teardown close the publisher before clearing so asynchronous or queued progress callbacks cannot emit after the clear.

### Preserve command output

Write control sequences without a newline and only through the eligible terminal output path. Keep the existing output and result behavior unchanged when the publisher is disabled; do not add sequences to JSON output.

## Risks / Trade-offs

- [Windows Terminal does not interpret OSC 9;4 in older or incompatible configurations] -> Limit emission to Windows Terminal sessions, document the supported host behavior, and retain the existing in-terminal progress display as the fallback.
- [A delayed callback could emit progress after command teardown] -> Serialize publisher updates and close the reporting session before sending the final clear sequence.
- [Tests that only exercise Spectre's rendered output may miss terminal sequences] -> Test the publisher through its injected writer and add command-level coverage for phase transitions, output gating, and cleanup.

## Migration Plan

No persisted data or command-line syntax changes are needed. The feature can be rolled back by removing the publisher and its command integrations; existing progress displays remain unaffected.
