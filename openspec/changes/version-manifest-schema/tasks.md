## 1. Version handling

- [ ] 1.1 Add a version read (`PRAGMA user_version`) and a "has tables" check to `ManifestSchema`, plus a too-new error carrying the found and supported versions; verify with a unit test that a database stamped 2 is reported as too new.
- [ ] 1.2 Make `initialize` create and stamp a new database, and for an unstamped one run the collation step then stamp 1, all in one transaction; verify with tests for a new database, an unstamped current-schema database and the `legacySchema` fixture (rows preserved, version 1).
- [ ] 1.3 Add the ordered migration step runner (empty list), each step committed with its version bump; verify with a test that injects a failing step and finds the version and data unchanged.
- [ ] 1.4 Check the version in `openReadOnly` and refuse a newer one without touching the file; verify the file is byte-identical after a refusal and after reading an unstamped database.

## 2. CLI surface

- [ ] 2.1 Map the too-new error to a hard error (exit 1, both versions on standard error) in the CLI error reporting; verify with command tests for `backup` and `history` against a database stamped 99.
- [ ] 2.2 Add a process test in `ProcessTests.fs` that stamps a database 99 and checks exit 1, the message on standard error and an untouched target.

## 3. Specs, naming and docs

- [ ] 3.1 Add spec-coverage test modules and methods named after the `manifest-versioning` requirements and scenarios; verify `SpecCoverageTests` passes.
- [ ] 3.2 Rename `FormatV0Target`, its `format-v0` corpus folder and the test wording to "unversioned target"; verify the full suite passes.
- [ ] 3.3 Document the schema version and the downgrade refusal in README.md; verify the text matches the spec.
- [ ] 3.4 Run the full test suite and `openspec validate version-manifest-schema`; verify both pass.
