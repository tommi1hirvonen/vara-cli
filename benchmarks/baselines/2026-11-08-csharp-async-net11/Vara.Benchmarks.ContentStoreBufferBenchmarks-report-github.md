```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 41,19 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4
  ShortRun : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | ContentSize | BufferSize | Mean       | Error     | StdDev   | Gen0     | Gen1     | Gen2     | Allocated  |
|------------------------------- |------------ |----------- |-----------:|----------:|---------:|---------:|---------:|---------:|-----------:|
| **HashWhileCopyingToBufferedFile** | **65536**       | **4096**       |   **498.0 μs** | **353.13 μs** | **19.36 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **16384**      |   **510.7 μs** |  **96.02 μs** |  **5.26 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **65536**      |   **506.1 μs** | **171.36 μs** |  **9.39 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **81920**      |   **541.8 μs** | **231.83 μs** | **12.71 μs** |   **9.7656** |        **-** |        **-** |   **81.37 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **262144**     |   **644.2 μs** | **384.49 μs** | **21.08 μs** | **166.0156** | **166.0156** | **166.0156** |  **513.58 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **1048576**    |   **665.0 μs** | **225.94 μs** | **12.38 μs** | **500.9766** | **500.0000** | **500.0000** | **2052.53 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **4096**       |   **863.1 μs** | **238.35 μs** | **13.06 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **16384**      |   **863.8 μs** | **398.85 μs** | **21.86 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **65536**      |   **847.8 μs** | **490.36 μs** | **26.88 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **81920**      |   **862.2 μs** | **567.20 μs** | **31.09 μs** |        **-** |        **-** |        **-** |    **1.34 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **262144**     | **1,023.1 μs** | **112.98 μs** |  **6.19 μs** | **166.0156** | **166.0156** | **166.0156** |  **513.58 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **1048576**    | **1,069.4 μs** |  **35.18 μs** |  **1.93 μs** | **500.0000** | **500.0000** | **500.0000** | **2049.96 KB** |
