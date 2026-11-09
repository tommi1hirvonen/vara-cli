```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 38,28 GB Available
.NET SDK 11.0.100-rc.3.26522.148
  [Host]   : .NET 11.0.0 (11.0.0-rc.3.26522.148, 11.0.26.52348), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0-rc.3.26522.148, 11.0.26.52348), X64 RyuJIT x86-64-v4

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method           | TransferConcurrency | FileCount | BytesPerFile | Mean      | Error      | StdDev    | Gen0      | Allocated |
|----------------- |-------------------- |---------- |------------- |----------:|-----------:|----------:|----------:|----------:|
| **ExecuteTransfers** | **1**                   | **8**         | **65536**        |  **13.05 ms** |  **12.275 ms** |  **0.673 ms** |         **-** |    **1.2 MB** |
| **ExecuteTransfers** | **1**                   | **8**         | **1048576**      |  **16.24 ms** |   **3.135 ms** |  **0.172 ms** |         **-** |    **1.2 MB** |
| **ExecuteTransfers** | **1**                   | **64**        | **65536**        | **219.59 ms** | **510.876 ms** | **28.003 ms** | **1000.0000** |   **9.53 MB** |
| **ExecuteTransfers** | **1**                   | **64**        | **1048576**      | **252.29 ms** | **204.122 ms** | **11.189 ms** | **1000.0000** |   **9.53 MB** |
| **ExecuteTransfers** | **4**                   | **8**         | **65536**        |  **22.41 ms** | **118.787 ms** |  **6.511 ms** |         **-** |    **1.2 MB** |
| **ExecuteTransfers** | **4**                   | **8**         | **1048576**      |  **46.63 ms** | **267.162 ms** | **14.644 ms** |         **-** |    **1.2 MB** |
| **ExecuteTransfers** | **4**                   | **64**        | **65536**        | **146.57 ms** | **536.791 ms** | **29.423 ms** | **1000.0000** |   **9.53 MB** |
| **ExecuteTransfers** | **4**                   | **64**        | **1048576**      | **149.70 ms** | **328.806 ms** | **18.023 ms** | **1000.0000** |   **9.52 MB** |
