```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9911/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 39,87 GB Available
.NET SDK 11.0.100
  [Host]   : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method         | FileCount | BytesPerFile | Mean      | Error        | StdDev    | Median    | Allocated  |
|--------------- |---------- |------------- |----------:|-------------:|----------:|----------:|-----------:|
| **RunFirstBackup** | **8**         | **65536**        |  **35.68 ms** |   **253.283 ms** | **13.883 ms** |  **27.67 ms** |  **664.88 KB** |
| **RunFirstBackup** | **8**         | **1048576**      |  **33.80 ms** |     **9.335 ms** |  **0.512 ms** |  **33.57 ms** |  **664.88 KB** |
| **RunFirstBackup** | **64**        | **65536**        | **152.61 ms** |   **152.223 ms** |  **8.344 ms** | **148.26 ms** | **4919.07 KB** |
| **RunFirstBackup** | **64**        | **1048576**      | **319.66 ms** | **1,418.729 ms** | **77.765 ms** | **355.96 ms** | **4918.46 KB** |
