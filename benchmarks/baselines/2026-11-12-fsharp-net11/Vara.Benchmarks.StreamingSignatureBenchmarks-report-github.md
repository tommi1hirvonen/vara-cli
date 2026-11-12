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
| Method                               | ContentSize | Mean       | Error       | StdDev    | Gen0   | Gen1   | Allocated |
|------------------------------------- |------------ |-----------:|------------:|----------:|-------:|-------:|----------:|
| **PrefixReplayStreamCopyToNull**         | **262144**      |   **3.116 μs** |   **0.1621 μs** | **0.0089 μs** | **0.0114** |      **-** |     **112 B** |
| PrefixReplayStreamReadPrefix         | 262144      |   1.044 μs |   0.0149 μs | 0.0008 μs | 0.0134 |      - |     112 B |
| PrefixReplayStreamReadAcrossBoundary | 262144      |   1.155 μs |   0.0620 μs | 0.0034 μs | 0.0134 |      - |     112 B |
| ComputeQuickHash                     | 262144      |   2.653 μs |   0.2341 μs | 0.0128 μs | 7.8735 |      - |   66320 B |
| ComputeQuickAndFullHash              | 262144      |  11.443 μs |   7.5334 μs | 0.4129 μs | 7.9956 | 1.9836 |   67248 B |
| ComputeQuickAndFullHashFromFile      | 262144      | 243.873 μs | 112.3321 μs | 6.1573 μs | 7.8125 |      - |   67434 B |
| **PrefixReplayStreamCopyToNull**         | **4194304**     |  **55.843 μs** |   **1.9963 μs** | **0.1094 μs** |      **-** |      **-** |     **112 B** |
| PrefixReplayStreamReadPrefix         | 4194304     |   1.025 μs |   0.0927 μs | 0.0051 μs | 0.0134 |      - |     112 B |
| PrefixReplayStreamReadAcrossBoundary | 4194304     |   1.159 μs |   0.0343 μs | 0.0019 μs | 0.0134 |      - |     112 B |
| ComputeQuickHash                     | 4194304     |   2.639 μs |   0.1512 μs | 0.0083 μs | 7.8735 |      - |   66320 B |
| ComputeQuickAndFullHash              | 4194304     | 150.987 μs |   8.3355 μs | 0.4569 μs | 7.8125 |      - |   67248 B |
| ComputeQuickAndFullHashFromFile      | 4194304     | 561.038 μs | 140.8824 μs | 7.7222 μs | 7.8125 |      - |   67434 B |
