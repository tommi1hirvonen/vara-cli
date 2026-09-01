## MODIFIED Requirements

### Requirement: Outcome severity is visually distinct
The system SHALL render any reported command outcome - a full success, a success with partial failures, or a hard error - in a visually distinct style per its severity, consistently across every command, so a user can tell an outcome's severity without reading its text closely. WHEN an outcome's report includes supporting detail beyond the outcome itself (for example, per-item counts, or a multi-line summary), the severity style SHALL apply to the outcome indicator only, and the supporting detail SHALL render in the default, unstyled text, so the report is not dominated by a single color.

#### Scenario: Command completes with no failures
- **WHEN** a command completes with no failures of any kind
- **THEN** the system reports the outcome in a bold, success-colored style (green)

#### Scenario: Command completes with partial failures
- **WHEN** a command completes overall but one or more individual items (for example, files) failed
- **THEN** the system reports the outcome in a style distinct from both a full success and a hard error (amber/yellow)

#### Scenario: Command reports a hard error
- **WHEN** any command reports a hard error that prevented it from completing
- **THEN** the system reports the error in a distinct error style (red), regardless of which command produced the error

#### Scenario: Outcome report includes multi-line supporting detail
- **WHEN** a command's reported outcome includes supporting detail beyond the outcome indicator itself (for example, a backup run's per-category file counts and byte totals)
- **THEN** only the outcome indicator is rendered in the severity-appropriate style, and the supporting detail renders in the default, unstyled text

## ADDED Requirements

### Requirement: Unclassified exceptions are rendered in the hard-error style
WHEN any command raises an exception that is not recognized as a known error with a friendly message, the system SHALL render that exception - including a formatted stack trace - to standard error using the hard-error severity style, rather than allowing it to propagate unhandled and produce a raw, unstyled stack trace.

#### Scenario: An unrecognized exception is raised
- **WHEN** a command raises an exception that the system does not recognize as one of its known, friendly-message error conditions
- **THEN** the system renders the exception's details, including its stack trace, to standard error in the hard-error style, and the command exits with a non-zero status

#### Scenario: A recognized exception is still handled as before
- **WHEN** a command raises an exception the system does recognize as a known error condition
- **THEN** the system continues to render only that exception's friendly message in the hard-error style, without a stack trace, exactly as before this capability's catch-all was added

### Requirement: Bordered widgets use a consistent rounded-corner style
The system SHALL render every bordered widget it displays - including tables and any future panel-style widget - with rounded corners, consistently across every command, so the terminal output presents one coherent visual identity rather than a mix of border styles.

#### Scenario: A command renders a table
- **WHEN** any command renders tabular output (for example, a listing of snapshots or file versions)
- **THEN** the table's border uses the rounded-corner style
