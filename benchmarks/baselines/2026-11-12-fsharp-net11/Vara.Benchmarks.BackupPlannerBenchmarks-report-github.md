```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9911/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 43,31 GB Available
.NET SDK 11.0.100
  [Host]   : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method            | ContentSize | IsMoveMatch | Mean     | Error       | StdDev   | Gen0   | Gen1   | Allocated |
|------------------ |------------ |------------ |---------:|------------:|---------:|-------:|-------:|----------:|
| **PlanMoveDetection** | **262144**      | **False**       | **243.4 μs** |    **44.97 μs** |  **2.46 μs** | **8.0566** |      **-** |  **67.19 KB** |
| **PlanMoveDetection** | **262144**      | **True**        | **266.0 μs** |    **90.29 μs** |  **4.95 μs** | **8.3008** | **1.9531** |  **68.23 KB** |
| **PlanMoveDetection** | **4194304**     | **False**       | **230.7 μs** |    **50.51 μs** |  **2.77 μs** | **8.0566** |      **-** |  **67.19 KB** |
| **PlanMoveDetection** | **4194304**     | **True**        | **752.1 μs** | **1,157.14 μs** | **63.43 μs** | **7.8125** | **0.9766** |  **68.23 KB** |
