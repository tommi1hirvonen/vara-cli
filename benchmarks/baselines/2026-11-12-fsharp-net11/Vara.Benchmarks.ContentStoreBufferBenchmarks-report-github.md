```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9911/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 43,31 GB Available
.NET SDK 11.0.100
  [Host]   : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | ContentSize | BufferSize | Mean       | Error       | StdDev   | Gen0     | Gen1     | Gen2     | Allocated  |
|------------------------------- |------------ |----------- |-----------:|------------:|---------:|---------:|---------:|---------:|-----------:|
| **HashWhileCopyingToBufferedFile** | **65536**       | **4096**       |   **619.6 μs** |    **32.20 μs** |  **1.76 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **16384**      |   **636.9 μs** |   **150.11 μs** |  **8.23 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **65536**      |   **649.8 μs** |   **197.23 μs** | **10.81 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **81920**      |   **639.2 μs** |   **302.75 μs** | **16.59 μs** |   **9.7656** |        **-** |        **-** |   **81.41 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **262144**     |   **730.7 μs** |   **350.23 μs** | **19.20 μs** | **166.0156** | **166.0156** | **166.0156** |  **513.62 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **1048576**    |   **786.3 μs** |   **106.89 μs** |  **5.86 μs** | **500.0000** | **500.0000** | **500.0000** | **2052.17 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **4096**       | **1,166.3 μs** |   **498.38 μs** | **27.32 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **16384**      | **1,149.5 μs** |   **912.08 μs** | **49.99 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **65536**      | **1,202.4 μs** | **1,089.42 μs** | **59.71 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **81920**      | **1,142.5 μs** |   **826.26 μs** | **45.29 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **262144**     | **1,220.1 μs** |   **195.38 μs** | **10.71 μs** | **166.0156** | **166.0156** | **166.0156** |  **513.62 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **1048576**    | **1,192.7 μs** |   **419.09 μs** | **22.97 μs** | **500.0000** | **500.0000** | **500.0000** |    **2050 KB** |
