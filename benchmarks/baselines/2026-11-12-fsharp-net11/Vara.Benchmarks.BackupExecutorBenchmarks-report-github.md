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
| Method           | TransferConcurrency | FileCount | BytesPerFile | Mean      | Error     | StdDev    | Median    | Allocated  |
|----------------- |-------------------- |---------- |------------- |----------:|----------:|----------:|----------:|-----------:|
| **ExecuteTransfers** | **1**                   | **8**         | **65536**        |  **16.24 ms** |  **10.55 ms** |  **0.578 ms** |  **16.51 ms** |   **574.7 KB** |
| **ExecuteTransfers** | **1**                   | **8**         | **1048576**      |  **47.16 ms** | **416.62 ms** | **22.836 ms** |  **36.14 ms** |   **574.7 KB** |
| **ExecuteTransfers** | **1**                   | **64**        | **65536**        | **239.66 ms** | **517.04 ms** | **28.341 ms** | **233.47 ms** | **4551.07 KB** |
| **ExecuteTransfers** | **1**                   | **64**        | **1048576**      | **448.33 ms** | **134.45 ms** |  **7.370 ms** | **447.11 ms** | **4550.46 KB** |
| **ExecuteTransfers** | **4**                   | **8**         | **65536**        |  **21.74 ms** | **197.99 ms** | **10.852 ms** |  **15.75 ms** |  **574.72 KB** |
| **ExecuteTransfers** | **4**                   | **8**         | **1048576**      |  **39.60 ms** | **327.30 ms** | **17.941 ms** |  **39.91 ms** |  **574.72 KB** |
| **ExecuteTransfers** | **4**                   | **64**        | **65536**        | **113.67 ms** | **356.90 ms** | **19.563 ms** | **104.06 ms** | **4551.07 KB** |
| **ExecuteTransfers** | **4**                   | **64**        | **1048576**      | **120.93 ms** | **336.50 ms** | **18.445 ms** | **126.73 ms** | **4550.46 KB** |
