```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 40,55 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4
  ShortRun : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method            | ContentSize | IsMoveMatch | Mean     | Error     | StdDev  | Gen0   | Gen1   | Allocated |
|------------------ |------------ |------------ |---------:|----------:|--------:|-------:|-------:|----------:|
| **PlanMoveDetection** | **262144**      | **False**       | **254.8 μs** | **114.53 μs** | **6.28 μs** | **8.3008** |      **-** |  **69.37 KB** |
| **PlanMoveDetection** | **262144**      | **True**        | **284.8 μs** |  **18.02 μs** | **0.99 μs** | **8.3008** |      **-** |  **70.19 KB** |
| **PlanMoveDetection** | **4194304**     | **False**       | **232.9 μs** |  **44.12 μs** | **2.42 μs** | **8.3008** |      **-** |  **69.37 KB** |
| **PlanMoveDetection** | **4194304**     | **True**        | **540.1 μs** |  **88.32 μs** | **4.84 μs** | **7.8125** | **0.9766** |  **70.19 KB** |
