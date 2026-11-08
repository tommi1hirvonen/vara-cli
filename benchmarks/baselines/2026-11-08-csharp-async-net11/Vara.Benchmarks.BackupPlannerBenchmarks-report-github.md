```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 41,19 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4
  ShortRun : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method            | ContentSize | IsMoveMatch | Mean     | Error     | StdDev   | Gen0   | Gen1   | Allocated |
|------------------ |------------ |------------ |---------:|----------:|---------:|-------:|-------:|----------:|
| **PlanMoveDetection** | **262144**      | **False**       | **229.5 μs** | **135.60 μs** |  **7.43 μs** | **8.3008** |      **-** |  **68.26 KB** |
| **PlanMoveDetection** | **262144**      | **True**        | **237.4 μs** |  **79.54 μs** |  **4.36 μs** | **8.3008** | **1.4648** |  **69.05 KB** |
| **PlanMoveDetection** | **4194304**     | **False**       | **242.0 μs** |  **28.02 μs** |  **1.54 μs** | **8.3008** |      **-** |  **68.26 KB** |
| **PlanMoveDetection** | **4194304**     | **True**        | **573.8 μs** | **391.08 μs** | **21.44 μs** | **7.8125** | **0.9766** |  **69.05 KB** |
