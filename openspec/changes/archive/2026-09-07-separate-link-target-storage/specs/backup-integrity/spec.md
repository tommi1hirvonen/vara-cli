## MODIFIED Requirements

### Requirement: Check command verifies stored content against the manifest
The system SHALL provide a `vara check --profile <name>` command that verifies every content blob referenced by any snapshot in the profile's manifest - across the full snapshot history, not only the current live mirror - against the content physically present in the profile's target. A symlink/junction entry recorded in the manifest SHALL NOT be treated as a referenced content blob, since it never had content placed in the store; only entries that represent actual stored content participate in this verification. By default the command SHALL re-read each referenced blob's full content and recompute its hash, reporting any blob whose recomputed hash does not match the hash recorded for it in the manifest as **corrupt**. A referenced blob that is not physically present in the store SHALL be reported as **missing**, regardless of mode. The command SHALL NOT modify the target, the manifest, or any stored content; it is read-only.

#### Scenario: All referenced content intact
- **WHEN** a user runs `vara check` for a profile and every blob referenced by its manifest is present and matches its recorded hash
- **THEN** the system reports that no problems were found and exits successfully

#### Scenario: Referenced blob missing from the store
- **WHEN** a user runs `vara check` for a profile and a blob referenced by at least one file version in the manifest is not physically present in the target's content store
- **THEN** the system reports that blob as missing, identifies at least one file path that references it, and exits with a non-zero, scriptable status

#### Scenario: Referenced blob present but corrupted
- **WHEN** a user runs `vara check` for a profile without `--quick`, and a blob referenced by the manifest is physically present but its recomputed content hash does not match the hash recorded for it
- **THEN** the system reports that blob as corrupt, identifies at least one file path that references it, and exits with a non-zero, scriptable status

#### Scenario: Unreferenced content present in the store
- **WHEN** a user runs `vara check` for a profile and a blob is physically present in the target's content store but is not referenced by any snapshot in the manifest
- **THEN** the system reports that blob as orphaned, informationally, without treating it as an error and without deleting it

#### Scenario: Profile contains a tracked symlink or junction
- **WHEN** a user runs `vara check` for a profile whose manifest records a symlink or junction entry, and every actual content blob referenced by the manifest is present and matches its recorded hash
- **THEN** the system does not report the symlink/junction entry as a missing or corrupt blob, and the run reports that no problems were found
