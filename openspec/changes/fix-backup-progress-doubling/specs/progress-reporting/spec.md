## MODIFIED Requirements

### Requirement: Incremental progress during in-flight file transfers
The system SHALL report transfer progress incrementally as a file's content is copied, rather than only once that file's transfer has fully completed, so that the displayed bytes-transferred, percentage, throughput, and ETA continue to advance while a single large file is still being transferred. This SHALL apply both to reading a source file's content into the store and to placing stored content at a mirror path via a real file copy (used when the target filesystem does not support hardlinks, or when a blob has reached its hard-link limit). The total-bytes-to-transfer figure used for live progress reporting SHALL equal the planned total bytes of source content to transfer, matching the dry-run estimate and the post-run completion summary. When placing content at a mirror path requires a fallback file copy (because the target filesystem does not support hardlinks, or a blob has reached its hard-link limit), the system SHALL apportion the reported transfer progress proportionally across both copy passes for that file (50% for reading the source content into the store, and 50% for copying the stored content into the mirror path), so that progress advances continuously across both passes without exceeding the planned total bytes or 100% over the course of the run.

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
- **THEN** the live progress display reports a total bytes figure equal to the planned source bytes to transfer, the cumulative transferred bytes advances across both copy passes to reach that same total upon completion, and the displayed percentage never exceeds 100% over the course of the run
