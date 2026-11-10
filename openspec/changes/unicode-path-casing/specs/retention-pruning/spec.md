## MODIFIED Requirements

### Requirement: Unreferenced content garbage collection
After removing eligible snapshot records, the system SHALL remove any stored content in the version store that is no longer referenced by any remaining snapshot or by the current live mirror. The record that makes a path's content current SHALL be determined with the same case-insensitive path matching as change detection, for the letters of every script, so that a path recorded under several casings has exactly one current record, and a path whose latest record is a deletion has none, whatever casing that deletion or any earlier record used. A record that is not current SHALL NOT keep its snapshot or its content alive.

#### Scenario: Content only referenced by pruned snapshots
- **WHEN** all snapshots referencing a piece of historical content are removed during a prune run
- **THEN** that content is removed from the version store, freeing its disk space

#### Scenario: Path deleted under another casing no longer keeps its snapshot or content
- **WHEN** the manifest holds a live record for `Äiti.txt` in an expired snapshot, a later live record for `äiti.txt`, and a still later deletion recorded for `äiti.txt`, and a prune run removes the expired snapshots
- **THEN** the expired snapshot is removed, and the content that only those records referenced is removed from the version store

#### Scenario: Current record of a path recorded under several casings is kept
- **WHEN** the manifest holds records for `Äiti.txt` and a later one for `äiti.txt`, the later one in a snapshot that is not expiring, and a prune run removes an older snapshot holding the `Äiti.txt` record
- **THEN** the older record is removed, the later record and the content it references stay, and the path remains in the current state
