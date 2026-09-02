## MODIFIED Requirements

### Requirement: Prune command
The system SHALL provide a `vara prune <profile>` command that requires only the profile name, applies that profile's configured retention policy, and removes the manifest records for snapshots eligible for removal. WHEN at least one snapshot is eligible for removal, the system SHALL NOT remove any snapshot records unless the user has explicitly authorized the run, either by passing an explicit override or by confirming an interactive prompt.

#### Scenario: Running prune with only a profile name
- **WHEN** a user runs the prune command for a profile with a configured retention policy, supplying only the profile name
- **THEN** the system evaluates the policy, and either proceeds without additional input (if no snapshots are eligible for removal) or prompts for confirmation before removing any eligible snapshot records, per the confirmation scenarios below

#### Scenario: No snapshots eligible for removal
- **WHEN** a user runs the prune command for a profile with a configured retention policy and zero snapshots are eligible for removal
- **THEN** the system evaluates the policy, makes no snapshot removals, and proceeds directly to garbage collection and reporting, without prompting for confirmation

#### Scenario: Snapshots eligible for removal, explicit override given
- **WHEN** a user runs the prune command with the `--yes`/`-y` option and at least one snapshot is eligible for removal
- **THEN** the system removes the eligible snapshot records without prompting for confirmation

#### Scenario: Snapshots eligible for removal, running interactively, no explicit override given
- **WHEN** at least one snapshot is eligible for removal, no explicit override is given, and the command is running with an interactive input stream
- **THEN** the system prompts the user for confirmation, stating how many snapshots will be permanently removed, defaulting to not pruning if the user provides no explicit answer

#### Scenario: User confirms the interactive prune prompt
- **WHEN** a user is prompted to confirm a prune run and responds affirmatively
- **THEN** the system removes the eligible snapshot records and proceeds with garbage collection as normal

#### Scenario: User declines the interactive prune prompt
- **WHEN** a user is prompted to confirm a prune run and responds negatively, or provides no answer
- **THEN** the system leaves all snapshot records and stored content unchanged, reports that the prune was cancelled, and exits successfully rather than reporting an error

#### Scenario: Snapshots eligible for removal, running non-interactively, no explicit override given
- **WHEN** at least one snapshot is eligible for removal, no explicit override is given, and the command is running with a non-interactive input stream
- **THEN** the system reports a clear error explaining that an explicit override is required, and does not remove any snapshot records or stored content
