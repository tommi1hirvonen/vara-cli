## 1. Windows sleep-prevention request

- [x] 1.1 Add an AOT-safe, disposable Windows system-required power-request implementation and verify native request creation, activation, release, handle closure, and activation-failure cleanup through focused tests.
- [x] 1.2 Add an injectable CLI boundary for acquiring the request and verify that activation and cleanup errors are reported to standard error without throwing over the backup outcome.

## 2. Backup command integration

- [x] 2.1 Scope the request around the real backup pipeline for JSON, interactive, and plain-output modes; verify all modes acquire and release it on success, cancellation, and exceptions.
- [x] 2.2 Keep dry-run outside the request scope and verify it does not acquire a request; verify request activation failure warns and still executes the real backup.

## 3. Validation

- [x] 3.1 Run the focused CLI test suite and verify Native AOT publish succeeds for the Windows target with the new interop.
- [x] 3.2 On Windows, run a sufficiently long backup and verify the system-required request is visible while the backup runs, clears after it ends, and does not request that the display remain on.
