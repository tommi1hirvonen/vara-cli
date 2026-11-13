```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9911/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 40,11 GB Available
.NET SDK 11.0.100
  [Host]   : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method            | EntryCount | Mean         | Error        | StdDev     | Gen0      | Gen1      | Gen2      | Allocated   |
|------------------ |----------- |-------------:|-------------:|-----------:|----------:|----------:|----------:|------------:|
| **Create**            | **20000**      |  **6,668.27 μs** |   **216.740 μs** |  **11.880 μs** | **1085.9375** |  **992.1875** |  **992.1875** |  **5396.99 KB** |
| ExpandAll         | 20000      |  6,855.19 μs | 2,992.031 μs | 164.003 μs | 1109.3750 |  992.1875 |  992.1875 |  7098.96 KB |
| TwentyCursorSteps | 20000      |     59.14 μs |     6.029 μs |   0.330 μs |   60.2417 |    2.9297 |         - |   492.19 KB |
| **Create**            | **200000**     | **94,552.67 μs** | **9,282.730 μs** | **508.818 μs** | **1666.6667** | **1000.0000** | **1000.0000** | **48628.48 KB** |
| ExpandAll         | 200000     | 99,559.45 μs | 9,363.814 μs | 513.262 μs | 2000.0000 | 1333.3333 | 1333.3333 | 62808.48 KB |
| TwentyCursorSteps | 200000     |     60.15 μs |    33.614 μs |   1.843 μs |   61.1572 |    2.9907 |         - |      500 KB |
