```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 36,66 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4


```
| Method | FileCount | Mean       | Error     | StdDev     | Ratio | RatioSD | Gen0     | Allocated  | Alloc Ratio |
|------- |---------- |-----------:|----------:|-----------:|------:|--------:|---------:|-----------:|------------:|
| **CSharp** | **256**       |   **9.542 ms** | **0.1593 ms** |  **0.1490 ms** |  **1.00** |    **0.00** |  **15.6250** |  **230.42 KB** |        **1.00** |
| FSharp | 256       |  12.115 ms | 0.6063 ms |  1.7396 ms |  1.27 |    0.18 |  15.6250 |  228.27 KB |        0.99 |
|        |           |            |           |            |       |         |          |            |             |
| **CSharp** | **4096**      | **172.550 ms** | **8.3692 ms** | **24.6768 ms** |  **1.00** |    **0.00** | **250.0000** | **3293.42 KB** |        **1.00** |
| FSharp | 4096      | 180.046 ms | 8.1770 ms | 24.1101 ms |  1.06 |    0.20 |        - | 3321.27 KB |        1.01 |
