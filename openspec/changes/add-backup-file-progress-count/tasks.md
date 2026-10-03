## 1. Report File-Transfer Counts

- [ ] 1.1 Extend backup progress data and pipeline reporting with the planned Add/Change total and successfully completed file count, keeping byte-based timing independent; verify with focused Vara.Application.Tests.
- [ ] 1.2 Add executor and pipeline tests for successful completion, failed transfers, metadata-only plans, and concurrent completions; verify the Vara.Application.Tests project passes.

## 2. Update Interactive Statistics

- [ ] 2.1 Render bytes and files on the first statistics row, throughput and ETA on the second, with stable field positions below the full-width bar; verify with BackupProgressColumn rendering tests.

## 3. Update Plain-Text Progress

- [ ] 3.1 Include transferred files out of planned files in non-interactive backup progress output while retaining the existing byte, percentage, throughput, and ETA values; verify with focused BackupCommand tests.
- [ ] 3.2 Run the Vara.Cli.Tests project to verify the complete backup progress presentation behavior.
