## 1. Add the binary guard to `ShowVersion`

- [x] 1.1 Add a `ShowBinaryContentException` (single-sided sibling of `DiffBinaryContentException`)
      to `Vara.Core.Snapshots.SnapshotExceptions`, and wire it into `ErrorReporting`'s known
      exception list the same way `DiffBinaryContentException` already is
- [x] 1.2 Add a `forceBinary` parameter to `SnapshotHistoryService.ShowVersion`; when `false` and
      `LooksBinary` detects binary content on the opened stream, throw `ShowBinaryContentException`
      before calling `CopyTo`; when `true`, skip the check entirely
- [x] 1.3 Add unit tests: showing text content behaves unchanged; showing binary content with
      `forceBinary: false` throws before any bytes are written to the destination stream; showing
      binary content with `forceBinary: true` streams it exactly as before; verify all pass

## 2. Wire up `ShowCommand`

- [x] 2.1 Add a `--force-binary` boolean option to `ShowCommand`
- [x] 2.2 Pass `Console.IsOutputRedirected || forceBinary` as `ShowVersion`'s `forceBinary`
      argument
- [x] 2.3 Add a `ShowCommand` test (or extend an existing one) covering: binary content refused
      when output is not redirected and `--force-binary` is not given; binary content streamed
      when `--force-binary` is given; verify both pass

## 3. Verification

- [x] 3.1 Run the full test suite and verify all tests pass
- [x] 3.2 Manually run `vara show` against a known binary-tracked file (a) with output attached to
      an interactive terminal and confirm the refusal error, (b) redirected to a file and confirm
      it streams successfully, and (c) with `--force-binary` to an interactive terminal and
      confirm it streams
