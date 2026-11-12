```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9911/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 43,31 GB Available
.NET SDK 11.0.100
  [Host]   : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method         | FileCount | BytesPerFile | Mean      | Error     | StdDev    | Allocated  |
|--------------- |---------- |------------- |----------:|----------:|----------:|-----------:|
| **RunFirstBackup** | **8**         | **65536**        |  **56.55 ms** | **372.75 ms** | **20.432 ms** |  **665.31 KB** |
| **RunFirstBackup** | **8**         | **1048576**      |  **52.47 ms** |  **76.81 ms** |  **4.210 ms** |   **664.7 KB** |
| **RunFirstBackup** | **64**        | **65536**        | **305.28 ms** | **706.39 ms** | **38.720 ms** | **4922.57 KB** |
| **RunFirstBackup** | **64**        | **1048576**      | **401.79 ms** | **933.88 ms** | **51.189 ms** | **4921.96 KB** |
