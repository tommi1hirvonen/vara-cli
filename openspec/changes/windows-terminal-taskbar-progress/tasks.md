## 1. Shared Progress Publisher

- [ ] 1.1 Add a testable CLI presentation publisher for Windows Terminal OSC 9;4 states and verify exact indeterminate, determinate, and clear sequences, including percentage clamping.
- [ ] 1.2 Gate publishing on Windows Terminal host detection, live terminal output, and human-readable mode; verify with focused tests that unsupported hosts and redirected or JSON output emit no sequences.

## 2. Command Integration

- [ ] 2.1 Connect backup progress to the publisher for scanning, transfer, and dry-run scanning; verify phase transitions and percentages in CLI tests.
- [ ] 2.2 Connect single-file and recursive restore progress to the publisher; verify determinate updates use the existing aggregate byte progress.
- [ ] 2.3 Connect integrity check and prune progress to the publisher; verify their indeterminate setup phases transition to existing determinate progress.
- [ ] 2.4 Ensure every covered command serializes updates and clears progress after success, cancellation, or failure; verify no late update can follow teardown.

## 3. Compatibility and Documentation

- [ ] 3.1 Add command-level tests proving JSON, redirected output, and non-Windows-Terminal behavior remain unchanged; run the focused CLI test suite.
- [ ] 3.2 Document Windows Terminal OSC 9;4 support and its host/version requirements in the relevant user documentation; verify the documented behavior matches the implementation.
- [ ] 3.3 Run a manual Windows Terminal check for indeterminate-to-determinate progress and indicator cleanup on completion, cancellation, and failure.
