```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9911/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 39,87 GB Available
.NET SDK 11.0.100
  [Host]   : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method            | ContentSize | IsMoveMatch | Mean     | Error    | StdDev   | Gen0   | Gen1   | Allocated |
|------------------ |------------ |------------ |---------:|---------:|---------:|-------:|-------:|----------:|
| **PlanMoveDetection** | **262144**      | **False**       | **192.8 μs** | **184.9 μs** | **10.14 μs** | **8.0566** |      **-** |  **67.19 KB** |
| **PlanMoveDetection** | **262144**      | **True**        | **250.8 μs** | **160.0 μs** |  **8.77 μs** | **8.3008** | **1.9531** |  **68.23 KB** |
| **PlanMoveDetection** | **4194304**     | **False**       | **199.8 μs** | **120.0 μs** |  **6.58 μs** | **8.0566** |      **-** |  **67.19 KB** |
| **PlanMoveDetection** | **4194304**     | **True**        | **538.7 μs** | **367.5 μs** | **20.14 μs** | **7.8125** | **0.9766** |  **68.23 KB** |
