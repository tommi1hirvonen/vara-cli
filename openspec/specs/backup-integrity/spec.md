# backup-integrity Specification

## Purpose

Lets a user verify that content physically stored in a profile's target still matches what the manifest expects, independently of source-side change detection, so a backup's restorability can be checked without waiting until a restore is attempted.

## Requirements

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

### Requirement: Graceful cancellation via Ctrl+C
The system SHALL intercept a single Ctrl+C (or equivalent interrupt signal) during a `vara check`
run and request a graceful stop instead of allowing the operating system to terminate the process
immediately: the run SHALL stop before verifying any further referenced blob, and SHALL then exit
deliberately, reporting that the run was cancelled, without treating this as an error and without
reporting a non-zero, scriptable "problems found" status on account of the cancellation itself.
Blobs already verified before cancellation SHALL be reported exactly as they would be for a
completed run. WHEN a second Ctrl+C is received while the graceful stop is still in progress, the
system SHALL terminate immediately without waiting for the blob currently being verified to
finish.

#### Scenario: Single Ctrl+C during verification
- **WHEN** a user presses Ctrl+C once while a check run is verifying referenced blobs against the
  manifest
- **THEN** the system finishes verifying the blob currently in progress, does not start verifying
  any further blob, reports that the run was cancelled, and exits successfully

#### Scenario: Cancelled run reports problems found before cancellation
- **WHEN** a user presses Ctrl+C during a check run after at least one missing or corrupt blob has
  already been found
- **THEN** the system still reports the problems found before cancellation, in addition to
  reporting that the run was cancelled

#### Scenario: Second Ctrl+C forces immediate termination
- **WHEN** a user presses Ctrl+C a second time while the system is still finishing verification of
  the blob in progress after a first Ctrl+C
- **THEN** the system terminates immediately without completing that verification
