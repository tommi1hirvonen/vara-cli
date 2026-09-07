## Why
`Profile` validates each source against the target root, but never checks sources against each
other. Two overlapping sources (for example `C:\Users\me` and `C:\Users\me\Documents`) both scan
the same on-disk files and, because a mirror path is derived from a file's absolute path rather
than which source found it, both sources produce the identical mirror path for the same file.
That yields two `Add`/`Change` operations targeting one mirror path in the same run - a race under
`transfer_concurrency > 1` - plus two manifest rows recorded for what is really one file.

## What Changes
- Extend `Profile`'s construction-time validation to reject a profile whose sources overlap each
  other (one source path equal to, an ancestor of, or a descendant of another source path in the
  same profile), using the same case-insensitive, trailing-separator-safe comparison the existing
  target-vs-source overlap check already uses.
- Report the offending pair of source paths in the validation error, consistent with how the
  existing target-vs-source overlap error identifies its offending paths.

## Capabilities

### Modified Capabilities
- `profile-config`: adds a new validation rule rejecting a profile whose own source paths overlap
  each other, alongside the existing target-vs-source overlap rule.

## Impact
- `Vara.Core.Configuration.Profile` (constructor validation).
- Profile-loading error reporting/tests (`Vara.Core.Tests`) covering the new rejection case.
- No change to already-valid profiles: a profile whose sources do not overlap continues to load
  exactly as before.
