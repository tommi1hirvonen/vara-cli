## MODIFIED Requirements

### Requirement: Flexible path input for history, restore, show, and diff
The system SHALL accept a path argument to the `history`, `restore`, `show`, and `diff` commands
in any of the following forms. WHEN the current working directory resolves to a location inside
the profile's mirror, the system SHALL try, in order: (1) the input resolved against the current
working directory to an absolute path, and, since that absolute path falls within the profile's
mirror, treated as a mirror-relative path by stripping the mirror root; (2) if that does not match
a recorded path, the input taken exactly as given, matched against recorded history literally; (3)
otherwise, treated as an absolute source path and converted to its mirror-relative form the same
way the backup pipeline derives mirror paths from source paths. WHEN the current working directory
does not resolve to a location inside the profile's mirror, the system SHALL try, in order: (1)
the input taken exactly as given, matched against recorded history literally; (2) otherwise,
resolved against the current working directory to an absolute path and, if that absolute path
happens to fall within the profile's mirror despite the working directory not being inside it (for
example, an absolute path typed directly), treated as a mirror-relative path by stripping the
mirror root; (3) otherwise, treated as an absolute source path and converted to its mirror-relative
form the same way the backup pipeline derives mirror paths from source paths. In every case, the
system uses the first interpretation that matches a recorded path; if none of these
interpretations matches a recorded path, the system reports that no history exists for the given
path.

#### Scenario: Path given in its mirror-relative form
- **WHEN** a user's current working directory is not inside the profile's mirror, and supplies a
  path that, taken literally, matches a path recorded in the profile's history
- **THEN** the system uses that literal path

#### Scenario: Path relative to a working directory inside the mirror
- **WHEN** a user's current working directory is inside the profile's mirror and supplies a path
  relative to that working directory (or an absolute path already inside the mirror) that, once
  resolved against the working directory and stripped of the mirror root, matches a path recorded
  in the profile's history
- **THEN** the system uses that cwd-relative interpretation, even if the input, taken literally,
  would also match a different path recorded elsewhere in the profile's history

#### Scenario: Path relative to a working directory inside the mirror, no cwd-relative match
- **WHEN** a user's current working directory is inside the profile's mirror, and the cwd-relative
  interpretation does not match a recorded path
- **THEN** the system falls back to trying the input literally, and then as an absolute source
  path, in that order

#### Scenario: Path relative to a working directory inside a source
- **WHEN** a user's current working directory is inside one of the profile's configured sources
  and supplies a path relative to that working directory that does not literally match a
  recorded path and does not resolve inside the mirror
- **THEN** the system resolves the supplied path against the working directory to an absolute
  source path, converts it to its mirror-relative form, and uses that as the resolved path

#### Scenario: Absolute source path given directly
- **WHEN** a user supplies an absolute path from one of the profile's sources (for example,
  copied from a file explorer or another tool) instead of its mirror-relative form
- **THEN** the system converts it to its mirror-relative form and uses that as the resolved path

#### Scenario: No interpretation matches recorded history
- **WHEN** none of the path's possible interpretations matches a path recorded in the profile's
  history
- **THEN** the system reports that no history exists for the given path
