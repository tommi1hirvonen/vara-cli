## MODIFIED Requirements

### Requirement: Tiered retention evaluation
The system SHALL evaluate a profile's configured retention policy by retaining, for each configured tier (daily, weekly, monthly, yearly), the most recent snapshot within each calendar bucket of that tier, up to the configured count of buckets, and marking all other snapshots outside the retained set as eligible for removal. Regardless of tier configuration or bucket math, the single most recent completed snapshot for the profile SHALL always be retained and SHALL NOT be marked eligible for removal.

#### Scenario: Snapshot within a retained daily bucket
- **WHEN** two snapshots exist for the same calendar day and the daily tier's bucket count has not been exceeded
- **THEN** only the most recent snapshot of that day is retained and the earlier one is eligible for removal

#### Scenario: All tiers configured to retain nothing
- **WHEN** a profile's retention policy has every tier's count set to zero
- **THEN** the most recent completed snapshot is still retained and is not marked eligible for removal, while all other completed snapshots are eligible for removal

#### Scenario: Most recent completed snapshot recorded no file changes
- **WHEN** the most recent completed snapshot recorded zero added, changed, moved, or deleted files
- **THEN** that snapshot is still retained and is not marked eligible for removal, so it continues to appear in snapshot history output
