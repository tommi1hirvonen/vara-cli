# retention-pruning Specification

## Purpose

Keeps a profile's version history within a bounded, predictable amount of disk space over time by expiring older snapshots according to a tiered schedule, while preserving representative history at coarser granularity further back in time.

## Requirements

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

### Requirement: Prune command
The system SHALL provide a `vara prune <profile>` command that requires only the profile name, applies that profile's configured retention policy, and removes the manifest records for snapshots eligible for removal.

#### Scenario: Running prune with only a profile name
- **WHEN** a user runs the prune command for a profile with a configured retention policy
- **THEN** the system evaluates the policy and removes eligible snapshot records without requiring any additional input

### Requirement: Unreferenced content garbage collection
After removing eligible snapshot records, the system SHALL remove any stored content in the version store that is no longer referenced by any remaining snapshot or by the current live mirror.

#### Scenario: Content only referenced by pruned snapshots
- **WHEN** all snapshots referencing a piece of historical content are removed during a prune run
- **THEN** that content is removed from the version store, freeing its disk space

### Requirement: Live mirror unaffected by pruning
Pruning SHALL never remove or alter files currently present in the live mirror; it SHALL only affect historical snapshot records and content no longer needed to support them.

#### Scenario: Pruning with current files present
- **WHEN** a prune run removes eligible historical snapshots
- **THEN** the files currently present in the live mirror remain unchanged and fully intact

### Requirement: No retention policy configured
WHEN a profile has no retention policy configured, the prune command SHALL take no action and SHALL clearly report that no policy is configured, rather than deleting all or none of the history by default assumption.

#### Scenario: Prune without a configured policy
- **WHEN** a user runs the prune command for a profile without a retention policy in its configuration
- **THEN** the system makes no deletions and reports that no retention policy is configured for that profile
