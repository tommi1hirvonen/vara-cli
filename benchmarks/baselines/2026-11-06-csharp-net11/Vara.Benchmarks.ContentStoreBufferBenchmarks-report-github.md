```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 40,55 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4
  ShortRun : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | ContentSize | BufferSize | Mean       | Error       | StdDev   | Gen0     | Gen1     | Gen2     | Allocated  |
|------------------------------- |------------ |----------- |-----------:|------------:|---------:|---------:|---------:|---------:|-----------:|
| **HashWhileCopyingToBufferedFile** | **65536**       | **4096**       |   **537.0 μs** |   **402.53 μs** | **22.06 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **16384**      |   **514.3 μs** |    **94.96 μs** |  **5.21 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **65536**      |   **526.9 μs** |   **129.59 μs** |  **7.10 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **81920**      |   **532.5 μs** |   **145.59 μs** |  **7.98 μs** |   **9.7656** |        **-** |        **-** |   **81.37 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **262144**     |   **660.9 μs** |   **272.43 μs** | **14.93 μs** | **166.0156** | **166.0156** | **166.0156** |  **513.58 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **1048576**    |   **683.1 μs** |   **259.05 μs** | **14.20 μs** | **500.9766** | **500.0000** | **500.0000** | **2052.44 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **4096**       |   **954.9 μs** |   **796.68 μs** | **43.67 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **16384**      |   **949.6 μs** |   **450.02 μs** | **24.67 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **65536**      | **1,391.1 μs** | **1,685.81 μs** | **92.40 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **81920**      |   **861.2 μs** |   **106.17 μs** |  **5.82 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **262144**     | **1,148.8 μs** | **1,197.48 μs** | **65.64 μs** | **166.0156** | **166.0156** | **166.0156** |  **513.58 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **1048576**    | **1,084.4 μs** |    **73.09 μs** |  **4.01 μs** | **500.0000** | **500.0000** | **500.0000** | **2049.96 KB** |
