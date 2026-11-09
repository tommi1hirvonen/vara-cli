# Release check

Date: 2026-11-09 11:26:53Z. Runs per scenario: 5, the first discarded (samples: 4).
Machine: AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M       ; Microsoft Windows 11 Enterprise; 63 GB RAM; scratch directory `C:\Users\TommiHirvonen\AppData\Local\Temp\gateb`.
Antimalware: real-time protection True, antimalware service True.
Baseline: `benchmarks\baselines\2026-11-09-fsharp-net11\release-check.json` (recorded 2026-11-09 11:24:12Z on AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M       ; Microsoft Windows 11 Enterprise; 63 GB RAM).

| Scenario | N | CPU median ms | CPU best ms | CPU IQR % | Wall median ms | Wall best ms | Peak working set MB |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 first backup, 2,000 files | 4 | 6,391 | 6,188 | 2.2 | 6,305 | 6,165 | 28.9 |
| 2 nothing changed | 4 | 109 | 109 | 7.1 | 142 | 138 | 17.8 |
| 3 large tree first backup | 4 | 13,055 | 13,000 | 0.4 | 12,984 | 12,904 | 28.2 |

Executable: 9,631,232 bytes.

Allocations are held separately by the BenchmarkDotNet baseline (budget 5%).

Verdict by rule:

- cpu    1 first backup, 2,000 files: run/baseline median 1.175, best 1.245 (limit 1.10 for both; IQR 2.2%): FAIL
- wall   1 first backup, 2,000 files: run/baseline median 1.176, best 1.254 (advisory; both above 1.15 means investigate): INVESTIGATE
- memory 1 first backup, 2,000 files: run/baseline peak working set 0.991 (recorded)
- cpu    2 nothing changed: run/baseline median 1.077, best 1.167 (limit 1.10 for both; IQR 7.1%): INCONCLUSIVE (IQR above 5%; run again on a quiet machine)
- wall   2 nothing changed: run/baseline median 1.062, best 1.058 (advisory; both above 1.15 means investigate): ok
- memory 2 nothing changed: run/baseline peak working set 0.993 (recorded)
- cpu    3 large tree first backup: run/baseline median 1.025, best 1.036 (limit 1.10 for both; IQR 0.4%): pass
- wall   3 large tree first backup: run/baseline median 1.020, best 1.022 (advisory; both above 1.15 means investigate): ok
- memory 3 large tree first backup: run/baseline peak working set 0.885 (limit 1.10): pass
- size: run/baseline executable 1.000 (limit 1.05): pass

**Overall: FAIL (see the rules above)**

