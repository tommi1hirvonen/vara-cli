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
The system SHALL provide a `vara prune <profile>` command that requires only the profile name, applies that profile's configured retention policy, and removes the manifest records for snapshots eligible for removal. WHEN at least one snapshot is eligible for removal, the system SHALL NOT remove any snapshot records unless the user has explicitly authorized the run, either by passing an explicit override or by confirming an interactive prompt. WHEN the user confirms an interactive prompt, the system SHALL remove only the snapshots that were eligible for removal at the time they were shown to the user; a snapshot that becomes eligible only after the prompt was shown SHALL NOT be removed by that run, and SHALL instead be evaluated again on a subsequent prune run.

#### Scenario: Running prune with only a profile name
- **WHEN** a user runs the prune command for a profile with a configured retention policy, supplying only the profile name
- **THEN** the system evaluates the policy, and either proceeds without additional input (if no snapshots are eligible for removal) or prompts for confirmation before removing any eligible snapshot records, per the confirmation scenarios below

#### Scenario: No snapshots eligible for removal
- **WHEN** a user runs the prune command for a profile with a configured retention policy and zero snapshots are eligible for removal
- **THEN** the system evaluates the policy, makes no snapshot removals, and proceeds directly to garbage collection and reporting, without prompting for confirmation

#### Scenario: Snapshots eligible for removal, explicit override given
- **WHEN** a user runs the prune command with the `--yes`/`-y` option and at least one snapshot is eligible for removal
- **THEN** the system removes the eligible snapshot records without prompting for confirmation, evaluating eligibility fresh at the time of removal

#### Scenario: Snapshots eligible for removal, running interactively, no explicit override given
- **WHEN** at least one snapshot is eligible for removal, no explicit override is given, and the command is running with an interactive input stream
- **THEN** the system prompts the user for confirmation, stating how many snapshots will be permanently removed, defaulting to not pruning if the user provides no explicit answer

#### Scenario: User confirms the interactive prune prompt
- **WHEN** a user is prompted to confirm a prune run and responds affirmatively
- **THEN** the system removes exactly the snapshots that were eligible for removal when the prompt was shown, and proceeds with garbage collection as normal

#### Scenario: A snapshot becomes eligible after the prompt was shown but before removal runs
- **WHEN** a user confirms an interactive prune prompt, and between the prompt being shown and the removal actually running, some other snapshot not shown to the user becomes newly eligible for removal (for example, because a concurrently completing backup run changes which snapshot counts as most recent)
- **THEN** the system does not remove that newly-eligible snapshot as part of this run, removing only the snapshots the user was actually shown and confirmed

#### Scenario: A confirmed snapshot no longer exists by the time removal runs
- **WHEN** a user confirms an interactive prune prompt, and by the time removal runs one of the confirmed snapshots has already been removed by some other means
- **THEN** the system removes the remaining confirmed snapshots that are still present, without treating the already-absent one as an error

#### Scenario: User declines the interactive prune prompt
- **WHEN** a user is prompted to confirm a prune run and responds negatively, or provides no answer
- **THEN** the system leaves all snapshot records and stored content unchanged, reports that the prune was cancelled, and exits successfully rather than reporting an error

#### Scenario: Snapshots eligible for removal, running non-interactively, no explicit override given
- **WHEN** at least one snapshot is eligible for removal, no explicit override is given, and the command is running with a non-interactive input stream
- **THEN** the system reports a clear error explaining that an explicit override is required, and does not remove any snapshot records or stored content

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
