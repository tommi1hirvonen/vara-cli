## ADDED Requirements

### Requirement: Prune progress indication
While a prune run is evaluating retention and determining unreferenced content, the system SHALL display an indeterminate progress indicator. Once the set of unreferenced blobs to remove during garbage collection is known, the system SHALL display a count-based progress indicator (per the `progress-reporting` capability's count-based progress requirement) showing blobs removed against the total number eligible, so a prune run against a large content store does not appear to hang with no feedback during its garbage-collection phase.

#### Scenario: Evaluating retention and determining unreferenced content
- **WHEN** a prune run is listing snapshots, evaluating the retention policy, or determining which stored content is unreferenced
- **THEN** the system displays an indeterminate progress indicator reflecting that work is underway

#### Scenario: Removing unreferenced blobs
- **WHEN** a prune run is removing unreferenced blobs from the content store during garbage collection
- **THEN** the system displays a count-based progress indicator showing the number of blobs removed against the total number eligible for removal, updating as each blob is removed

#### Scenario: No unreferenced content to remove
- **WHEN** a prune run determines that no stored content is unreferenced after removing eligible snapshot records
- **THEN** the system completes without displaying a count-based progress indicator for zero items
