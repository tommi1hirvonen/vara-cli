## ADDED Requirements

### Requirement: Snapshot removal is durable before content is removed
The system SHALL make the removal of snapshot records durable - committed to persistent storage, not only accepted by the operating system's cache - before it removes any stored content in the same run. As a result, an abrupt loss of power or of the operating system at any point after the first piece of content has been removed SHALL NOT leave a remaining snapshot record that references content the run has already removed. WHEN a prune run removes no snapshot records, there is nothing to make durable and content removal proceeds as before.

#### Scenario: Snapshot removal is on persistent storage before the first content removal
- **WHEN** a prune run has removed eligible snapshot records and is about to remove its first unreferenced content
- **THEN** the removal of those snapshot records is already part of the manifest's persistent state, so that losing everything the manifest had not yet flushed would not bring those records back

#### Scenario: No remaining record references removed content after an abrupt loss
- **WHEN** a prune run is interrupted by an abrupt loss of power after it has removed some unreferenced content
- **THEN** every snapshot record that remains after the machine restarts references only content that is still stored
