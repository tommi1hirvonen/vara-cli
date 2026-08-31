## Purpose

Guarantees that a published `vara.exe` can be copied and run as a single standalone file on supported Windows versions, without a companion native SQLite library.

## ADDED Requirements

### Requirement: No companion native SQLite library
The published `vara.exe` SHALL perform all SQLite-backed operations (backup execution, snapshot history, retention pruning) without requiring a separate native SQLite library file to be present alongside the executable.

#### Scenario: Running the executable alone in a directory
- **WHEN** `vara.exe` is the only file copied into a directory on a supported Windows version and a command that touches the snapshot repository (e.g. a backup run) is invoked
- **THEN** the command completes successfully, with SQLite functionality resolved entirely from the operating system

### Requirement: Documented minimum OS boundary
The system SHALL document that it requires a Windows version providing the OS-supplied system SQLite library (Windows 10 version 1903 or later, Windows 11, or Windows Server 2022 or later) in order to run.

#### Scenario: Unsupported Windows version
- **WHEN** `vara.exe` is run on a Windows version that does not provide the OS-supplied system SQLite library
- **THEN** SQLite-backed functionality fails to initialize, and this is a documented, known limitation of that OS version rather than an unexpected defect
