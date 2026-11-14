## Context

See proposal.md for motivation. The manifest is created by `ManifestSchema.initialize` (`CREATE TABLE IF NOT EXISTS`, then `migrateCollationIfNeeded`, which inspects `sqlite_master`). `openReadOnly` runs no initialization, by design: a reader never creates or alters `profile.db`. Pre-collation databases lack both the collation and the `link_target` column. The target layout (`.vara/versions`, `.vara/tmp`, the lock directory, XxHash128 content names, `quick_hash_scheme`) has no version marker of its own; "format version 0" appears only as a test label (`FormatV0Target`).

## Goals / Non-Goals

**Goals:** one integer version, ordered transactional migrations, downgrade protection on every open path, no visible change for existing users.

**Non-Goals:** a pre-migration backup copy of `profile.db` (add it with the first destructive migration); a separate version for the layout; any migration beyond stamping, since none exists yet; showing the version in `check` or `history` output.

## Decisions

1. **`PRAGMA user_version`, not a metadata table.** It lives in the file header, is covered by the surrounding transaction, and can be read on a read-only connection. A table would need creating before it can be read, which breaks the rule that a reader never writes. Alternative: a `schema_info` table, rejected for those reasons.
2. **Version is an integer starting at 1; 0 means "not stamped".** A fresh file also reads 0, so the two are told apart by whether `snapshots` exists: no tables is a new database (create, stamp current), tables with 0 is an unstamped version 1.
3. **Version 1 is the schema after the collation migration.** The unstamped path keeps running `migrateCollationIfNeeded` before stamping, so every stamped database has the same shape. Structural detection stays only for that step and does not grow.
4. **Migrations are an ordered list of `(fromVersion, script)`.** Each runs in one transaction that ends with `PRAGMA user_version = n+1`. The list is empty today. Scripts are written out in full, as the existing comment on `migrateCollation` requires.
5. **One version for the manifest and the layout.** `profile.db` lives in the target and is the only per-target state that changes shape, so a layout change is delivered as a migration step that also moves files. This avoids two counters that must agree. Alternative: a marker file in `.vara`, rejected as a second source of truth.
6. **Refusal on both open paths.** `openAt` and `openReadOnly` read the version first and raise a new error carrying the found and supported versions. The CLI error reporting maps it to a hard error: exit 1, message on standard error, no writes. `openReadOnly` does not stamp and does not migrate; an older unstamped file simply opens.

## Risks / Trade-offs

- [A future version 2 forces read-only commands to handle version 1] → Decide then between tolerating version 1 and telling the user to run a backup first. The first migration must settle this.
- [Two processes migrate at once] → Writable opens already happen under the run lock, and the stamp is idempotent.
- [A pre-collation database stamped 1 by mistake] → The collation step runs before the stamp, in one transaction.

## Migration Plan

No user action. Existing databases are stamped on the first backup or other writable open. Returning to an older build is safe for version 1 data, because that build ignores `user_version`. This stops being true from the first migration onward.
