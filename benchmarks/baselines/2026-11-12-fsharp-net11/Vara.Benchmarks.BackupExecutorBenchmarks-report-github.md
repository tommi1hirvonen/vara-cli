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
| Method           | TransferConcurrency | FileCount | BytesPerFile | Mean      | Error      | StdDev    | Median    | Allocated  |
|----------------- |-------------------- |---------- |------------- |----------:|-----------:|----------:|----------:|-----------:|
| **ExecuteTransfers** | **1**                   | **8**         | **65536**        |  **16.42 ms** |  **13.025 ms** |  **0.714 ms** |  **16.34 ms** |   **574.7 KB** |
| **ExecuteTransfers** | **1**                   | **8**         | **1048576**      |  **25.90 ms** |  **91.282 ms** |  **5.003 ms** |  **23.19 ms** |  **574.38 KB** |
| **ExecuteTransfers** | **1**                   | **64**        | **65536**        | **128.77 ms** |   **4.055 ms** |  **0.222 ms** | **128.84 ms** | **4551.07 KB** |
| **ExecuteTransfers** | **1**                   | **64**        | **1048576**      | **324.78 ms** |  **43.369 ms** |  **2.377 ms** | **325.58 ms** | **4550.46 KB** |
| **ExecuteTransfers** | **4**                   | **8**         | **65536**        |  **20.53 ms** | **233.672 ms** | **12.808 ms** |  **13.25 ms** |  **574.72 KB** |
| **ExecuteTransfers** | **4**                   | **8**         | **1048576**      |  **25.13 ms** | **153.151 ms** |  **8.395 ms** |  **22.70 ms** |  **574.74 KB** |
| **ExecuteTransfers** | **4**                   | **64**        | **65536**        |  **96.38 ms** | **113.401 ms** |  **6.216 ms** |  **94.10 ms** | **4551.09 KB** |
| **ExecuteTransfers** | **4**                   | **64**        | **1048576**      | **153.58 ms** | **960.605 ms** | **52.654 ms** | **132.77 ms** | **4550.82 KB** |
