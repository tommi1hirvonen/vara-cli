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
| Method           | TransferConcurrency | FileCount | BytesPerFile | Mean      | Error       | StdDev    | Gen0      | Allocated |
|----------------- |-------------------- |---------- |------------- |----------:|------------:|----------:|----------:|----------:|
| **ExecuteTransfers** | **1**                   | **8**         | **65536**        |  **25.09 ms** |    **35.08 ms** |  **1.923 ms** |         **-** |   **1.22 MB** |
| **ExecuteTransfers** | **1**                   | **8**         | **1048576**      |  **18.54 ms** |    **12.26 ms** |  **0.672 ms** |         **-** |   **1.22 MB** |
| **ExecuteTransfers** | **1**                   | **64**        | **65536**        | **369.63 ms** | **1,362.54 ms** | **74.685 ms** | **1000.0000** |   **9.68 MB** |
| **ExecuteTransfers** | **1**                   | **64**        | **1048576**      | **384.90 ms** |   **688.86 ms** | **37.759 ms** | **1000.0000** |   **9.68 MB** |
| **ExecuteTransfers** | **4**                   | **8**         | **65536**        |  **62.34 ms** |    **92.66 ms** |  **5.079 ms** |         **-** |   **1.22 MB** |
| **ExecuteTransfers** | **4**                   | **8**         | **1048576**      |  **65.67 ms** |   **474.45 ms** | **26.006 ms** |         **-** |   **1.22 MB** |
| **ExecuteTransfers** | **4**                   | **64**        | **65536**        | **224.42 ms** |   **755.78 ms** | **41.427 ms** | **1000.0000** |   **9.69 MB** |
| **ExecuteTransfers** | **4**                   | **64**        | **1048576**      | **142.43 ms** |   **453.80 ms** | **24.874 ms** | **1000.0000** |   **9.68 MB** |
