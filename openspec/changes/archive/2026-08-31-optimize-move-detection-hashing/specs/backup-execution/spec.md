## ADDED Requirements

### Requirement: Move detection avoids unbounded reads for non-matching candidates
WHEN comparing an added file against same-size deleted candidates during move detection, the system SHALL rule out a candidate using no more than a small bounded read of the added file's content whenever the candidate's previously recorded content signature makes a match impossible, escalating to a full-content read and comparison only when a match remains possible after that bounded read. This SHALL rely only on content the system durably recorded for the deleted candidate at the time it was last backed up; it SHALL NOT require re-reading the deleted candidate's content, which is no longer assumed to exist at its original location. A file's final move-versus-add classification SHALL still be governed by an exact full-content match, unaffected by this optimization.

#### Scenario: Same-size, differing-content candidate ruled out cheaply
- **WHEN** an added file shares its size with one or more deleted candidates, and a small bounded read of the added file's content already differs from every one of those candidates' recorded signatures
- **THEN** move detection rules out those candidates without reading the rest of the added file's content, and the file is treated as a new addition

#### Scenario: Full content match still required to confirm a move
- **WHEN** an added file's bounded-read signature matches a same-size deleted candidate's previously recorded signature
- **THEN** move detection reads the remainder of the added file's content and confirms an exact match against the candidate's full recorded content hash before classifying the file as moved

#### Scenario: Recorded signature unavailable for a candidate
- **WHEN** a deleted candidate has no usable previously recorded content signature (for example, because it was recorded before this capability existed, or under an incompatible recording scheme)
- **THEN** move detection falls back to a full-content read and comparison for that candidate instead of failing or misclassifying the file
