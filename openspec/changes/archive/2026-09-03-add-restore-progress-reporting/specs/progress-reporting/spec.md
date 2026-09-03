## MODIFIED Requirements

### Requirement: Incremental progress during in-flight file transfers
The system SHALL report transfer progress incrementally as a file's content is copied, rather than only once that file's transfer has fully completed, so that the displayed bytes-transferred, percentage, throughput, and ETA continue to advance while a single large file is still being transferred. This SHALL apply to reading a source file's content into the store, to placing stored content at a mirror path via a real file copy (used when the target filesystem does not support hardlinks, or when a blob has reached its hard-link limit), and to extracting a historical version's content to a restore destination. When placing content at every mirror path is known in advance to require a real copy (the target filesystem does not support hardlinks at all), the system SHALL account for that additional copy pass in the total-bytes-to-transfer figure used for the live percentage and ETA, so the displayed percentage remains meaningful rather than exceeding 100% over the course of the run.

#### Scenario: Single large file transfer in progress
- **WHEN** a backup run is transferring a file large enough that its transfer takes a perceptible amount of time
- **THEN** the displayed bytes-transferred, percentage, throughput, and ETA continue to advance while that file's transfer is still in progress, rather than remaining unchanged until the file completes

#### Scenario: Two large files transferred in sequence
- **WHEN** a backup run transfers two large files one after another
- **THEN** the displayed progress advances continuously throughout both files' transfers, rather than jumping only when each file completes

#### Scenario: Large file placed at its mirror path via a real copy
- **WHEN** a backup run places a large stored blob at a mirror path using a real file copy rather than a hardlink (because the target filesystem does not support hardlinks, or the blob has reached its hard-link limit)
- **THEN** the displayed progress continues to advance while that copy is still in progress, rather than remaining unchanged until it completes

#### Scenario: Target filesystem without hardlink support does not overshoot 100%
- **WHEN** a backup run's target filesystem does not support hardlinks, so every transferred file's content is copied once into the local store and once more onto the mirror
- **THEN** the displayed percentage reflects both copy passes' bytes against a total that accounts for both, so it does not exceed 100% over the course of the run

#### Scenario: Large historical file extraction in progress
- **WHEN** a restore command is extracting a historical version of a file large enough that its extraction takes a perceptible amount of time
- **THEN** the displayed bytes-copied, percentage, and ETA continue to advance while that extraction is still in progress, rather than remaining unchanged until it completes
