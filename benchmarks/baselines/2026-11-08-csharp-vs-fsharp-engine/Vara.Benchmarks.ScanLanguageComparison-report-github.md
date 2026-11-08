```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 34,9 GB Available
.NET SDK 11.0.100-rc.3.26522.148
  [Host]   : .NET 11.0.0 (11.0.0-rc.3.26522.148, 11.0.26.52348), X64 RyuJIT x86-64-v4
  ShortRun : .NET 11.0.0 (11.0.0-rc.3.26522.148, 11.0.26.52348), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method | FileCount | Mean       | Error     | StdDev    | Ratio | RatioSD | Gen0     | Allocated  | Alloc Ratio |
|------- |---------- |-----------:|----------:|----------:|------:|--------:|---------:|-----------:|------------:|
| **CSharp** | **256**       |  **11.208 ms** | **17.415 ms** | **0.9546 ms** |  **1.00** |    **0.00** |  **15.6250** |  **230.42 KB** |        **1.00** |
| FSharp | 256       |   7.004 ms |  1.123 ms | 0.0615 ms |  0.63 |    0.05 |  23.4375 |  230.57 KB |        1.00 |
|        |           |            |           |           |       |         |          |            |             |
| **CSharp** | **4096**      | **154.035 ms** | **25.799 ms** | **1.4142 ms** |  **1.00** |    **0.00** | **250.0000** | **3293.42 KB** |        **1.00** |
| FSharp | 4096      | 124.548 ms | 43.214 ms | 2.3687 ms |  0.81 |    0.01 | 200.0000 | 3233.57 KB |        0.98 |
