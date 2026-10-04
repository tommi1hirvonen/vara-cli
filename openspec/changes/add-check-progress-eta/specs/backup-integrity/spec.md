## ADDED Requirements

### Requirement: Check reports workload progress and estimated time remaining
The system SHALL report progress while verifying referenced blobs, including a percentage and the number of blobs checked out of the total. During full verification, the percentage SHALL be weighted by the manifest sizes of distinct referenced blobs and SHALL advance as content is read; a missing blob SHALL count as checked when its absence is determined. During `--quick`, the percentage SHALL be based on the number of referenced blobs checked. The system SHALL report an estimated time remaining based on observed work in the current check, rather than assuming all blobs take equal time in full-verification mode. The estimate SHALL be presented as unavailable or calculating until enough work has been observed to calculate it. Live progress SHALL use a progress bar; when live display is unavailable, the command SHALL report plain-text progress containing the percentage and ETA. The enumeration phase SHALL remain visibly indeterminate until the set of referenced blobs and their total workload are known.

#### Scenario: Full check reports byte-weighted progress
- **WHEN** a user runs a full check and referenced blobs are being read and hashed
- **THEN** progress reports the percentage weighted by manifest blob sizes, the checked/total blob count, and an ETA estimated using observed hash-read throughput and per-blob processing time

#### Scenario: Missing blob advances full-check progress
- **WHEN** a full check determines that a referenced blob is missing
- **THEN** that blob is counted as checked and its manifest size is counted as completed work, without reporting bytes as read for the missing blob

#### Scenario: Quick check reports count-based progress and ETA
- **WHEN** a user runs `vara check --quick` and referenced blobs are being checked for presence
- **THEN** progress percentage is based on blobs checked out of total, and the ETA is estimated from observed per-blob processing time

#### Scenario: Estimate is not yet available
- **WHEN** a check has not observed enough verification work to estimate its remaining duration
- **THEN** the progress display identifies the ETA as calculating or unavailable rather than presenting a misleading duration

#### Scenario: Empty or zero-size workload completes cleanly
- **WHEN** a check has no referenced blobs, or full verification has no positive manifest byte total
- **THEN** progress reaches 100 percent on completion and the command does not divide by zero or display an invalid percentage

#### Scenario: Redirected output reports progress
- **WHEN** a check runs without a live-capable console
- **THEN** appended plain-text progress updates include percentage, checked/total blob count, and ETA availability or estimate
