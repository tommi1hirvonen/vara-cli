## Why

Long-running commands already calculate and display meaningful progress inside the terminal, but Windows Terminal's tab and taskbar indicators do not reflect that activity. Exposing the same progress externally gives users useful status while the terminal is minimized or in the background, including during phases where a percentage is not yet known.

## What Changes

- Publish progress to Windows Terminal using its OSC 9;4 progress sequences for commands that already report live progress.
- Map known completion percentages to determinate taskbar progress and unknown-work phases to an indeterminate indicator.
- Clear the terminal progress state when a command finishes, is cancelled, or fails, without interfering with JSON output or redirected streams.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `progress-reporting`: Extend existing progress reporting to Windows Terminal's taskbar and tab indicators across backup, restore, integrity check, and prune operations.

## Impact

- Affected command progress/reporting paths in `Vara.Cli`, including backup, restore, check, and prune.
- A shared terminal progress publisher and focused CLI tests may be needed; no application API or package dependency change is expected.
- Windows Terminal visual integration depends on its support for OSC 9;4. Other terminals and redirected output should retain current behavior.
