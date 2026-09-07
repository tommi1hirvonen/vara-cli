## Purpose

Lets a user verify that content physically stored in a profile's target still matches what the manifest expects, independently of source-side change detection, so a backup's restorability can be checked without waiting until a restore is attempted.

## ADDED Requirements

### Requirement: Check command verifies stored content against the manifest
The system SHALL provide a `vara check --profile <name>` command that verifies every content blob referenced by any snapshot in the profile's manifest - across the full snapshot history, not only the current live mirror - against the content physically present in the profile's target. By default the command SHALL re-read each referenced blob's full content and recompute its hash, reporting any blob whose recomputed hash does not match the hash recorded for it in the manifest as **corrupt**. A referenced blob that is not physically present in the store SHALL be reported as **missing**, regardless of mode. The command SHALL NOT modify the target, the manifest, or any stored content; it is read-only.

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

### Requirement: Quick mode trades thoroughness for speed
The system SHALL provide a `--quick` option to `vara check` that verifies only that each referenced blob is physically present in the store, without reading its content or recomputing its hash, so a user can trade full corruption detection for a faster presence-only check on a large backup.

#### Scenario: Quick check skips content re-hashing
- **WHEN** a user runs `vara check --quick` for a profile
- **THEN** the system checks only for each referenced blob's presence in the store, reports any missing blob exactly as the default mode would, and does not report a corrupt blob whose content has changed but is still present

### Requirement: Check exits with a scriptable status
The system SHALL exit with a distinct, non-zero status when `vara check` finds at least one missing or corrupt blob, distinct from the status used for an unrelated command failure, so the command can be used in an automated health-check without parsing its text output.

#### Scenario: Problems found
- **WHEN** a `vara check` run reports at least one missing or corrupt blob
- **THEN** the command's exit status is non-zero and distinguishes this outcome from an unrelated hard error (for example, a missing profile)

#### Scenario: No problems found
- **WHEN** a `vara check` run reports zero missing or corrupt blobs (orphaned blobs, if any, do not count as a problem)
- **THEN** the command exits successfully
