## Why

`FileSystemContentStore.PlaceAtMirrorPath` hardlinks a mirror entry directly to its content-store blob whenever the target filesystem supports hardlinks (the common case on NTFS). Because a hardlinked mirror file and the blob are the same physical file, an in-place edit made directly in the mirror (for example, opening a file in Notepad and saving) silently rewrites the content-store blob - corrupting that file's entire historical version record and every other mirror path sharing that same content, with no warning. This directly conflicts with the mirror's explicitly marketed purpose as a normal, Explorer-browsable folder: a backup that can be silently corrupted by browsing it is not safely browsable. Marking hardlinked mirror files read-only closes this gap by making naive in-place overwrites fail instead of corrupting shared content, while leaving a deliberate, well-known recovery path (copying files out, or clearing the read-only flag recursively via Explorer's folder Properties dialog) available for a user who intends to convert a restored/migrated mirror into an editable working copy.

## What Changes

- `PlaceAtMirrorPath` sets `FileAttributes.ReadOnly` on a mirror entry whenever it is placed via a hardlink (both the normal hardlink path and the case where a hardlink attempt succeeds after a previous per-blob failure).
- A mirror entry placed via the real-copy fallback (no hardlink support, or a blob's per-file hard-link limit reached) is left writable as today, since that entry is an independent physical copy and an in-place edit of it cannot corrupt the content-store blob or any other mirror path or historical version.
- When a mirror path's placement kind changes between runs (hardlink to copy-fallback, or copy-fallback back to hardlink, per the existing "Graceful degradation when a blob's hard-link limit is reached" requirement), the read-only attribute is set or cleared to match the new placement's kind rather than left stale from a prior run.
- `MoveMirrorEntry` and `RemoveFromMirror` continue to work on a read-only hardlinked entry (`File.Move`/`File.Delete` are unaffected by the source's own read-only attribute in .NET on Windows), so move and delete detection are unaffected by this change.

## Capabilities

### Modified Capabilities
- `backup-execution`: adds a requirement that a hardlinked mirror entry is marked read-only, and that a copy-fallback mirror entry remains writable, including when a mirror path's placement kind changes between runs.

## Impact

- `src/Vara.Infrastructure/Storage/FileSystemContentStore.cs` (`PlaceAtMirrorPath`): set/clear `FileAttributes.ReadOnly` based on which placement path (hardlink vs. copy) was used for a given call.
- Test doubles implementing `IContentStore` (`tests/Vara.Core.Tests/Abstractions/PortAbstractionsTests.cs`, `tests/Vara.Application.Tests/Backup/Fakes.cs`, `tests/Vara.IntegrationTests/Backup/Fakes.cs`) are unaffected in signature; `FileSystemContentStoreTests` gains coverage for the new attribute behavior.
- No breaking change to `IContentStore`'s public surface - this is an internal behavior change to `PlaceAtMirrorPath`'s filesystem side effects, observable only via the mirror file's attributes.
