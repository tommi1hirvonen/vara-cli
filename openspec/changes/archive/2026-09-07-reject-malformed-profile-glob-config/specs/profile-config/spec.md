## MODIFIED Requirements

### Requirement: Profile structure validation
Each profile SHALL define a name, a target root path, and one or more sources. Each source SHALL define a path and MAY define a recursive flag (default `true`), an exclude list, and glob include/exclude patterns. WHEN a source's exclude list or either glob pattern list is present but is not a list of plain string entries - for example, a scalar value instead of a list, or a list containing a nested mapping or sequence instead of a string - the system SHALL reject the profile with a validation error identifying the profile, the offending field, and the problem, rather than silently treating the malformed field as absent or silently dropping the offending entries.

#### Scenario: Missing required field
- **WHEN** a profile in the configuration file omits its target path or defines zero sources
- **THEN** the system reports a validation error identifying the profile and the missing field, and performs no action for that profile

#### Scenario: Source without recursive flag
- **WHEN** a source entry does not specify a recursive flag
- **THEN** the system treats that source as recursive by default

#### Scenario: Exclude field given as a scalar instead of a list
- **WHEN** a source's `exclude`, `include_globs`, or `exclude_globs` field is present in the configuration file but its value is a scalar (for example, `exclude: "*.tmp"`) rather than a list
- **THEN** the system reports a validation error identifying the profile, the offending source, the field name, and that a list was expected, and performs no action for that profile

#### Scenario: Exclude list contains a non-string entry
- **WHEN** a source's `exclude`, `include_globs`, or `exclude_globs` field is a list but one of its entries is a mapping or a nested sequence rather than a plain string
- **THEN** the system reports a validation error identifying the profile, the offending source, the field name, and the offending entry, and performs no action for that profile

#### Scenario: Well-formed exclude/glob lists accepted
- **WHEN** a source's `exclude`, `include_globs`, and `exclude_globs` fields are each either absent or a list of plain string entries
- **THEN** the system accepts the profile and applies the specified entries without reporting a validation error
