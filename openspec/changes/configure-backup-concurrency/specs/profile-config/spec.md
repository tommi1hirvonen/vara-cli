## ADDED Requirements

### Requirement: Concurrency configuration
Each profile MAY define a concurrency configuration specifying independent scan and transfer concurrency limits. Each configured concurrency value SHALL be a positive whole number; the system SHALL reject a value that is not a valid positive whole number with a validation error identifying the profile, the field, and the offending value, rather than raising an unhandled exception. A profile without a configured value for a given concurrency setting SHALL use the system's default for that setting.

#### Scenario: Profile without concurrency configuration
- **WHEN** a profile's configuration omits a concurrency section
- **THEN** backup runs for that profile use the system's default scan and transfer concurrency limits

#### Scenario: Scan concurrency configured
- **WHEN** a profile's configuration sets `scan_concurrency` to a positive whole number
- **THEN** backup runs for that profile bound move-detection scan concurrency to that number instead of the default

#### Scenario: Transfer concurrency configured
- **WHEN** a profile's configuration sets `transfer_concurrency` to a positive whole number
- **THEN** backup runs for that profile bound transfer concurrency to that number instead of the default

#### Scenario: Concurrency value is not a valid integer
- **WHEN** a profile's configuration sets `scan_concurrency` or `transfer_concurrency` to a value that cannot be parsed as a whole number (for example, `abc`)
- **THEN** the system reports a validation error identifying the profile, the field name, and the invalid value, and performs no action for that profile

#### Scenario: Concurrency value is zero or negative
- **WHEN** a profile's configuration sets `scan_concurrency` or `transfer_concurrency` to zero or a negative number
- **THEN** the system reports a validation error identifying the profile, the field name, and the invalid value, and performs no action for that profile
