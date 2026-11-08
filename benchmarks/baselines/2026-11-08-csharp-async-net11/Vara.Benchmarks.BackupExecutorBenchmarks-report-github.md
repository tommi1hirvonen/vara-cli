```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 41,19 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4
  ShortRun : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method           | TransferConcurrency | FileCount | BytesPerFile | Mean      | Error     | StdDev    | Median    | Gen0      | Allocated |
|----------------- |-------------------- |---------- |------------- |----------:|----------:|----------:|----------:|----------:|----------:|
| **ExecuteTransfers** | **1**                   | **8**         | **65536**        |  **35.31 ms** | **282.01 ms** | **15.458 ms** |  **27.47 ms** |         **-** |   **1.21 MB** |
| **ExecuteTransfers** | **1**                   | **8**         | **1048576**      |  **29.17 ms** |  **11.03 ms** |  **0.604 ms** |  **29.18 ms** |         **-** |   **1.21 MB** |
| **ExecuteTransfers** | **1**                   | **64**        | **65536**        | **212.58 ms** | **237.58 ms** | **13.023 ms** | **208.12 ms** | **1000.0000** |   **9.68 MB** |
| **ExecuteTransfers** | **1**                   | **64**        | **1048576**      | **239.63 ms** |  **19.30 ms** |  **1.058 ms** | **239.67 ms** | **1000.0000** |   **9.68 MB** |
| **ExecuteTransfers** | **4**                   | **8**         | **65536**        |  **36.94 ms** | **216.99 ms** | **11.894 ms** |  **41.17 ms** |         **-** |   **1.22 MB** |
| **ExecuteTransfers** | **4**                   | **8**         | **1048576**      |  **40.56 ms** | **564.22 ms** | **30.927 ms** |  **26.77 ms** |         **-** |   **1.21 MB** |
| **ExecuteTransfers** | **4**                   | **64**        | **65536**        | **182.33 ms** | **640.60 ms** | **35.114 ms** | **182.20 ms** | **1000.0000** |   **9.68 MB** |
| **ExecuteTransfers** | **4**                   | **64**        | **1048576**      | **177.49 ms** | **765.86 ms** | **41.980 ms** | **175.88 ms** | **1000.0000** |   **9.68 MB** |
