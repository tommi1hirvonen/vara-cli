## Why

An automatic Windows sleep during a long backup can interrupt file transfers and leave the backup run incomplete. Vara should ask Windows to remain awake while a real backup is running, while allowing normal display timeout and restoring ordinary sleep behavior as soon as the run ends.

## What Changes

- Request that Windows prevent automatic system sleep for the lifetime of a real backup run.
- Release the request on every exit path, including completion, cancellation, and errors.
- If the request cannot be activated, report a warning to stderr and continue the backup.
- Do not request sleep prevention for dry runs.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `backup-execution`: specify scoped prevention of automatic system sleep during real backup runs and best-effort behavior when Windows cannot honor the request.

## Impact

- `Vara.Cli` backup command and Windows power-management interop; no changes to the backup pipeline, SQLite storage, or dependencies are expected.
- Backup-execution requirements and CLI tests.
- Windows may still honor user-initiated sleep actions and platform power policies; this request does not keep the display on or guarantee indefinite wakefulness.
