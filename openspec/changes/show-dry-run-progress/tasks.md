## 1. Dry-Run Progress Display

- [ ] 1.1 Run human-readable dry-run planning inside the existing indeterminate scan display when live progress is supported, and verify through CLI tests that the indicator is active during planning and stops before the summary.
- [ ] 1.2 Preserve direct planning for JSON and non-live output paths, and verify CLI tests assert JSON stdout contains only the summary object and redirected output does not render a progress display.

## 2. Regression Verification

- [ ] 2.1 Verify existing dry-run summary and error-reporting behavior remains unchanged by running the focused `Vara.Cli.Tests` backup command tests.
