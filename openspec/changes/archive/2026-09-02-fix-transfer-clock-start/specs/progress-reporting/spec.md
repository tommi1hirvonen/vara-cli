## MODIFIED Requirements

### Requirement: Lightweight operations reported separately from data transfer
The system SHALL distinguish operations that do not transfer file content (for example, moves, hardlinks, and deletions) from byte transfers in its progress and summary reporting, so near-instant operations do not distort throughput or ETA figures. This SHALL include excluding the wall-clock time such operations take from the elapsed-time basis used to compute throughput and ETA, not merely excluding their (zero) byte contribution, so that a batch of lightweight operations preceding byte transfer does not permanently depress the run's reported throughput or inflate its reported ETA.

#### Scenario: Backup run consisting mostly of moved files
- **WHEN** a backup run's changes are dominated by files moved to new paths rather than content changes
- **THEN** the reported throughput and ETA reflect the actual bytes transferred, not the size of the moved files

#### Scenario: Lightweight operations precede byte transfer
- **WHEN** a backup run performs a batch of moves and/or deletions before any file content is transferred
- **THEN** the time spent on that batch is excluded from the elapsed-time basis used to compute throughput and ETA, so that once byte transfer begins, the displayed throughput reflects only the byte-transfer phase's own elapsed time rather than being diluted by the preceding batch's duration
