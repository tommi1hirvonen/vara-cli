## MODIFIED Requirements

### Requirement: Profile selection by name
Commands that operate on a single profile SHALL accept the profile name as an argument, and
SHALL fail clearly if the named profile does not exist. Commands limited to browsing or
restoring recorded history MAY instead be run without a profile name, in which case the system
SHALL attempt to resolve the profile by walking upward from the current working directory
looking for a `.vara\profile.db` file, the same way version control tools locate their own
metadata directory; this resolution SHALL succeed without requiring `~/.vara/profiles.yml` to
exist or define the resolved profile. If no such file is found before the filesystem root is
reached, the system SHALL report a clear error stating that a profile name is required.

#### Scenario: Unknown profile name
- **WHEN** a user runs a command against a profile name that does not exist in the configuration
  file
- **THEN** the system reports an error identifying the unknown profile name and performs no
  action

#### Scenario: Profile name omitted, working directory inside a profile's target root
- **WHEN** a user runs a browsing or restoring command without a profile name, and the current
  working directory is at or below a directory containing a `.vara\profile.db` file
- **THEN** the system resolves that directory as the profile's target root and proceeds without
  needing `~/.vara/profiles.yml` to exist

#### Scenario: Profile name omitted, working directory outside any profile's target root
- **WHEN** a user runs a browsing or restoring command without a profile name, and no `.vara\
  profile.db` file is found by walking upward from the current working directory to the
  filesystem root
- **THEN** the system reports a clear error stating that a profile name is required, and performs
  no action

#### Scenario: Profile name given explicitly while inside a profile's target root
- **WHEN** a user runs a browsing or restoring command with an explicit profile name, regardless
  of whether the current working directory is also inside a profile's target root
- **THEN** the system resolves the profile by the given name via `~/.vara/profiles.yml` (or the
  configured alternate path), rather than by the working directory
