```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 40,55 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4
  ShortRun : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method         | FileCount | BytesPerFile | Mean      | Error     | StdDev    | Gen0      | Allocated |
|--------------- |---------- |------------- |----------:|----------:|----------:|----------:|----------:|
| **RunFirstBackup** | **8**         | **65536**        |  **19.55 ms** |  **12.67 ms** |  **0.694 ms** |         **-** |   **1.26 MB** |
| **RunFirstBackup** | **8**         | **1048576**      |  **23.75 ms** |  **10.95 ms** |  **0.600 ms** |         **-** |   **1.27 MB** |
| **RunFirstBackup** | **64**        | **65536**        | **142.15 ms** | **243.88 ms** | **13.368 ms** | **1000.0000** |   **9.86 MB** |
| **RunFirstBackup** | **64**        | **1048576**      | **176.81 ms** | **229.51 ms** | **12.580 ms** | **1000.0000** |   **9.92 MB** |
