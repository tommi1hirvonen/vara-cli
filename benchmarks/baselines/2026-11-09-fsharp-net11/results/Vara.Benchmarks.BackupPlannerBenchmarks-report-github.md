```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 38,28 GB Available
.NET SDK 11.0.100-rc.3.26522.148
  [Host]   : .NET 11.0.0 (11.0.0-rc.3.26522.148, 11.0.26.52348), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0-rc.3.26522.148, 11.0.26.52348), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method            | ContentSize | IsMoveMatch | Mean     | Error       | StdDev    | Gen0   | Gen1   | Allocated |
|------------------ |------------ |------------ |---------:|------------:|----------:|-------:|-------:|----------:|
| **PlanMoveDetection** | **262144**      | **False**       | **225.2 μs** |    **32.96 μs** |   **1.81 μs** | **8.0566** |      **-** |  **67.19 KB** |
| **PlanMoveDetection** | **262144**      | **True**        | **247.9 μs** |    **71.64 μs** |   **3.93 μs** | **8.3008** | **1.9531** |  **68.23 KB** |
| **PlanMoveDetection** | **4194304**     | **False**       | **198.3 μs** |   **299.87 μs** |  **16.44 μs** | **8.0566** |      **-** |  **67.19 KB** |
| **PlanMoveDetection** | **4194304**     | **True**        | **695.9 μs** | **3,265.88 μs** | **179.01 μs** | **7.8125** | **0.9766** |  **68.23 KB** |
