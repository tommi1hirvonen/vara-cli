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
| Method           | FileCount | Mean       | Error       | StdDev   | Gen0     | Gen1    | Allocated  |
|----------------- |---------- |-----------:|------------:|---------:|---------:|--------:|-----------:|
| **ScanAndEnumerate** | **256**       |   **748.4 μs** |    **66.96 μs** |  **3.67 μs** |  **27.3438** |  **0.9766** |  **227.43 KB** |
| **ScanAndEnumerate** | **4096**      | **2,223.0 μs** | **1,043.57 μs** | **57.20 μs** | **386.7188** | **66.4063** | **3170.43 KB** |
