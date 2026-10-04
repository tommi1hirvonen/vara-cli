## 1. Workload and service progress

- [x] 1.1 Extend the manifest repository contract to return one size per distinct referenced content hash, using existing `file_versions` data; verify repeated historical references do not inflate totals and inconsistent sizes use the documented deterministic rule in repository tests.
- [x] 1.2 Extend integrity-check progress to expose the total manifest byte weight, completed blob count, and bytes consumed during full verification; verify missing, corrupt, and present blobs update progress without changing check findings or cancellation behavior.
- [x] 1.3 Track bytes read during hashing and publish throttled in-blob progress; verify a large blob produces intermediate byte-progress reports and final progress never exceeds its manifest weight.

## 2. ETA and CLI presentation

- [x] 2.1 Implement run-local full-check ETA from observed hash/read throughput and per-blob overhead, and quick-check ETA from observed per-blob timing; verify both estimators remain unavailable until measurements exist and handle empty and zero-size workloads.
- [x] 2.2 Update live check progress to show a percentage bar, checked/total blob count, and mode-appropriate ETA while preserving indeterminate enumeration; verify live rendering for full, quick, missing-blob, and empty-manifest cases.
- [x] 2.3 Update redirected check progress lines to include percentage, checked/total count, and calculating or estimated ETA; verify plain-output coverage in CLI tests.

## 3. Validation

- [x] 3.1 Run focused integrity-service and check-command tests, then the solution test suite; verify all tests pass and existing check outcomes remain unchanged.
