## 1. Column layout

- [x] 1.1 Add a fixed-width leading label column to `BackupProgressColumn.RenderBarRow` (both scan and transfer roles), sized to the longer of `" Scanning files..."` / `" Backing up..."`, and verify via `BackupProgressColumnTests` that the bar's rendered width is identical between a scan-role and a bar-role task at the same console width
- [x] 1.2 Change `RenderScan`'s trailing text from the label to a blank placeholder of the same fixed width the transfer phase's percent text occupies, and verify a unit test asserts the placeholder is blank (not synthetic percentage-like text)
- [x] 1.3 Add `"Backing up..."` as the transfer phase's leading label text (replacing today's percent-only trailing text, which moves to the placeholder's former position on the right), and verify a unit test asserts the label renders on the left and the percentage still renders on the right

## 2. Verification

- [x] 2.1 Update or add `BackupProgressColumnTests` cases covering: label position (left) for both roles, placeholder-vs-percent content on the right for each role, and bar-width equality between scan and transfer rows at a fixed console width
- [x] 2.2 Run `dotnet test` for `Vara.Cli.Tests` and confirm all tests pass
- [x] 2.3 Manually run `vara backup <profile>` against a profile with enough changed content to observe both phases, and confirm visually that the label sits left of the bar in both phases and the bar does not visibly change length between phases
