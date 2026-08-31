## MODIFIED Requirements

### Requirement: Retention policy configuration
Each profile MAY define a tiered retention policy consisting of the number of daily, weekly, monthly, and yearly snapshots to retain. A profile without a configured retention policy SHALL NOT have retention pruning available until one is configured. Each configured retention count SHALL be a non-negative whole number; the system SHALL reject a retention count that is not a valid non-negative whole number with a validation error identifying the profile, the field, and the offending value, rather than raising an unhandled exception.

#### Scenario: Profile without a retention policy
- **WHEN** a profile's configuration omits a retention policy
- **THEN** commands that depend on retention configuration report that no retention policy is configured for that profile, rather than assuming a default policy

#### Scenario: Retention count is not a valid integer
- **WHEN** a profile's retention policy sets `keep_daily`, `keep_weekly`, `keep_monthly`, or `keep_yearly` to a value that cannot be parsed as a whole number (for example, `abc`)
- **THEN** the system reports a validation error identifying the profile, the field name, and the invalid value, and performs no action for that profile

#### Scenario: Retention count is negative
- **WHEN** a profile's retention policy sets `keep_daily`, `keep_weekly`, `keep_monthly`, or `keep_yearly` to a negative number
- **THEN** the system reports a validation error identifying the profile, the field name, and the invalid value, and performs no action for that profile
