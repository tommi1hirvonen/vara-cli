## ADDED Requirements

### Requirement: Configurable backup-stage concurrency
The system SHALL bound the move-detection scan stage's and the file-transfer stage's concurrency independently, using a profile's configured scan and transfer concurrency limits (per the profile-config capability) when present. When a profile does not configure a stage's concurrency limit, the system SHALL fall back to that stage's own default rather than sharing a single default between stages: the scan stage's default SHALL scale with the number of available processors, while the transfer stage's default SHALL be a fixed low value suited to a target disk that performs best when written to by one stream at a time.

#### Scenario: Default transfer concurrency serializes transfers
- **WHEN** a profile does not configure `transfer_concurrency`
- **THEN** the backup run's transfer stage processes at most one file addition or change at a time

#### Scenario: Default scan concurrency scales with available processors
- **WHEN** a profile does not configure `scan_concurrency`
- **THEN** the backup run's move-detection scan stage may process multiple candidate files concurrently, up to the number of available processors

#### Scenario: Configured transfer concurrency overrides the default
- **WHEN** a profile configures `transfer_concurrency` to a value greater than the default
- **THEN** the backup run's transfer stage may process up to that many file additions or changes concurrently

#### Scenario: Configured scan concurrency overrides the default
- **WHEN** a profile configures `scan_concurrency` to a specific value
- **THEN** the backup run's move-detection scan stage bounds its concurrency to that value
