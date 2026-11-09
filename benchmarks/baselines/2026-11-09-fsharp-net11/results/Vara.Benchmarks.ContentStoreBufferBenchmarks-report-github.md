```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 38,28 GB Available
.NET SDK 11.0.100-rc.3.26522.148
  [Host]   : .NET 11.0.0 (11.0.0-rc.3.26522.148, 11.0.26.52348), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0-rc.3.26522.148, 11.0.26.52348), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | ContentSize | BufferSize | Mean       | Error       | StdDev    | Gen0     | Gen1     | Gen2     | Allocated  |
|------------------------------- |------------ |----------- |-----------:|------------:|----------:|---------:|---------:|---------:|-----------:|
| **HashWhileCopyingToBufferedFile** | **65536**       | **4096**       |   **817.3 μs** |   **340.68 μs** |  **18.67 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **16384**      |   **799.4 μs** |   **372.04 μs** |  **20.39 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **65536**      |   **823.6 μs** |   **765.93 μs** |  **41.98 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **81920**      |   **780.5 μs** |    **27.47 μs** |   **1.51 μs** |   **9.7656** |        **-** |        **-** |   **81.41 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **262144**     | **1,059.5 μs** |   **176.43 μs** |   **9.67 μs** | **166.0156** | **166.0156** | **166.0156** |  **513.62 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **1048576**    | **1,117.6 μs** |   **340.66 μs** |  **18.67 μs** | **500.0000** | **500.0000** | **500.0000** | **2052.69 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **4096**       | **1,641.1 μs** |   **813.05 μs** |  **44.57 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **16384**      | **1,635.3 μs** |   **532.38 μs** |  **29.18 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **65536**      | **1,673.6 μs** |   **818.23 μs** |  **44.85 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **81920**      | **1,639.7 μs** | **2,042.47 μs** | **111.95 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **262144**     | **1,952.4 μs** | **1,910.19 μs** | **104.70 μs** | **166.0156** | **166.0156** | **166.0156** |  **513.62 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **1048576**    | **1,891.3 μs** |   **756.41 μs** |  **41.46 μs** | **500.0000** | **500.0000** | **500.0000** |    **2050 KB** |
