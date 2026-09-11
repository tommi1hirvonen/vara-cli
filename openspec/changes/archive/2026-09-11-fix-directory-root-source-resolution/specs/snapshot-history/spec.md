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

The same resolution order applies to the directory argument accepted by `browse` and by
`restore --recursive`, with one adjustment. A directory argument of `.` or blank denotes the
profile's mirror root, which is always a valid directory to browse or restore regardless of
whether anything is tracked under it - unlike a file path or a non-root directory name, which
only match literally when something is actually recorded there. Because of this, taking `.` or
blank completely literally would always succeed at step (1) of the "current working directory
does not resolve to a location inside the profile's mirror" ordering above, and so would never
give the absolute-source-path interpretation - step (3) - a chance to run. To keep `.`/blank
consistent with every other path form, WHEN the directory argument is `.` or blank AND the
current working directory does not resolve to a location inside the profile's mirror, the system
SHALL instead try, in order: (1) the current working directory resolved to an absolute source
path and converted to its mirror-relative form the same way the backup pipeline derives mirror
paths from source paths, used if that mirror-relative location has any recorded history; (2)
otherwise, the mirror root itself, taken literally - and in this fallback case, the system SHALL
also report, briefly and clearly, that the current directory has no recorded history and the
mirror root is being shown instead, so the fallback is not mistaken for the directory the user
asked about.

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

#### Scenario: Browsing or recursively restoring the current directory from within a source
- **WHEN** a user runs `browse .` (or omits the directory argument, which defaults to `.`) or
  `restore --recursive .` with a current working directory that is inside one of the profile's
  configured sources rather than inside the profile's mirror, and that working directory maps to
  a mirror-relative location with recorded history
- **THEN** the system lists or restores that source-mapped directory, not the mirror root

#### Scenario: Current directory outside the mirror has no recorded history
- **WHEN** a user runs `browse .` (or omits the directory argument) or `restore --recursive .`
  with a current working directory that is inside one of the profile's configured sources, and
  that working directory's source-mapped location has no recorded history
- **THEN** the system falls back to listing or restoring the mirror root, and reports that the
  current directory has no recorded history so the mirror-root fallback is not mistaken for the
  directory the user asked about

#### Scenario: Non-root directory argument from within a source is unaffected
- **WHEN** a user supplies a non-root, non-blank directory argument to `browse` or
  `restore --recursive` from a current working directory that is inside one of the profile's
  configured sources
- **THEN** the system resolves it using the existing literal-then-absolute-source-path order,
  unchanged by this requirement's `.`/blank adjustment
