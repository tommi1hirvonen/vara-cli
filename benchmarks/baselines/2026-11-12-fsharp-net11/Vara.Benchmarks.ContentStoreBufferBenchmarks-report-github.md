```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9911/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 39,87 GB Available
.NET SDK 11.0.100
  [Host]   : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | ContentSize | BufferSize | Mean       | Error     | StdDev   | Gen0     | Gen1     | Gen2     | Allocated  |
|------------------------------- |------------ |----------- |-----------:|----------:|---------:|---------:|---------:|---------:|-----------:|
| **HashWhileCopyingToBufferedFile** | **65536**       | **4096**       |   **509.9 μs** | **270.80 μs** | **14.84 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **16384**      |   **499.8 μs** | **376.71 μs** | **20.65 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **65536**      |   **489.4 μs** | **308.73 μs** | **16.92 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **81920**      |   **496.8 μs** | **162.95 μs** |  **8.93 μs** |   **9.7656** |        **-** |        **-** |   **81.41 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **262144**     |   **621.4 μs** |  **96.00 μs** |  **5.26 μs** | **166.0156** | **166.0156** | **166.0156** |  **513.62 KB** |
| **HashWhileCopyingToBufferedFile** | **65536**       | **1048576**    |   **631.3 μs** | **556.76 μs** | **30.52 μs** | **500.0000** | **500.0000** | **500.0000** | **2052.55 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **4096**       |   **864.0 μs** |  **80.17 μs** |  **4.39 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **16384**      |   **883.6 μs** | **413.64 μs** | **22.67 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **65536**      |   **893.2 μs** | **342.66 μs** | **18.78 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **81920**      |   **896.6 μs** | **374.04 μs** | **20.50 μs** |        **-** |        **-** |        **-** |    **1.38 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **262144**     | **1,025.9 μs** |  **37.66 μs** |  **2.06 μs** | **166.0156** | **166.0156** | **166.0156** |  **513.62 KB** |
| **HashWhileCopyingToBufferedFile** | **1048576**     | **1048576**    |   **992.8 μs** | **613.55 μs** | **33.63 μs** | **500.0000** | **500.0000** | **500.0000** |    **2050 KB** |
