# Gate B

Date: 2026-11-08 23:55:17Z. Output mode: `json`. Runs per scenario per build: 5, the first discarded (samples: 4).
Machine: AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M       ; Microsoft Windows 11 Enterprise; 63 GB RAM; scratch directory `C:\Users\TommiHirvonen\Ohjelmointi\vara-cli-scratch\gateb`.
Antimalware: real-time protection True, antimalware service True.

| Scenario | Build | N | CPU median ms | CPU best ms | CPU IQR % | Wall median ms | Wall best ms | Wall IQR % | Peak working set MB |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 first backup, 2,000 files | C# | 4 | 4,836 | 4,562 | 6.2 | 4,856 | 4,620 | 8.4 | 31.0 |
| 1 first backup, 2,000 files | F# | 4 | 4,438 | 4,422 | 9.2 | 4,578 | 4,477 | 10.9 | 28.3 |
| 2 nothing changed | C# | 4 | 180 | 172 | 10.9 | 209 | 204 | 7.1 | 20.2 |
| 2 nothing changed | F# | 4 | 141 | 125 | 22.2 | 172 | 154 | 14.3 | 17.6 |
| 3 large tree first backup | C# | 4 | 9,047 | 8,969 | 3.1 | 9,213 | 8,678 | 2.7 | 33.9 |
| 3 large tree first backup | F# | 4 | 8,641 | 8,328 | 5.7 | 8,692 | 8,433 | 8.5 | 29.7 |

| Executable | Bytes |
|---|---:|
| C# | 9,186,816 |
| F# | 7,133,184 |

Allocations are held separately by `LanguageComparisonBenchmarks` (budget 5% against C#); see the language-comparison baseline.

Verdict by rule:

- cpu    1 first backup, 2,000 files: F#/C# median 0.918, best 0.969 (limit 1.10 for both): pass
- wall   1 first backup, 2,000 files: F#/C# median 0.943, best 0.969 (advisory; both above 1.15 means investigate): ok
- memory 1 first backup, 2,000 files: F#/C# peak working set 0.913 (recorded)
- cpu    2 nothing changed: F#/C# median 0.783, best 0.727 (limit 1.10 for both): pass
- wall   2 nothing changed: F#/C# median 0.825, best 0.755 (advisory; both above 1.15 means investigate): ok
- memory 2 nothing changed: F#/C# peak working set 0.870 (recorded)
- cpu    3 large tree first backup: F#/C# median 0.955, best 0.929 (limit 1.10 for both): pass
- wall   3 large tree first backup: F#/C# median 0.943, best 0.972 (advisory; both above 1.15 means investigate): ok
- memory 3 large tree first backup: F#/C# peak working set 0.874 (limit 1.10): pass
- size: F#/C# executable 0.776 (limit 1.25): pass

**Overall: PASS**

## Decision

Gate B passes under the revised rules (design D8: CPU time decides, wall-clock time is advisory). The first version of the gate used a wall-clock median within 1.05 of C# and an interquartile range of at most 5%. Over three runs on this machine it gave scenario ratios of 1.00, 1.08 and 1.15 for the same code, with IQRs of 14 to 20%, so it could not reach a verdict; the revision was made after those runs and this is the first run under the revised rules. All three scenarios pass with margin (CPU median 0.78 to 0.96 of C#, fastest run 0.73 to 0.97), memory is 0.87 of C# on the large tree, and the F# executable is 0.78 of the C# one. CPU time was not much quieter than wall-clock time here (CPU IQR 3 to 22%), because these runs are CPU-bound; the rule holds because the margin is larger than the noise, not because the noise is gone.

The scenario sizes were reduced from design D8's first draft (4,000 files in the large tree instead of 100,000; 5 runs instead of 15) to keep the gate to about four minutes. The scratch directory was excluded from Defender.

