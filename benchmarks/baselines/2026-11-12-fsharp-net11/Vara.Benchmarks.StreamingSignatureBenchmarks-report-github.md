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
| Method                               | ContentSize | Mean         | Error           | StdDev       | Gen0   | Gen1   | Allocated |
|------------------------------------- |------------ |-------------:|----------------:|-------------:|-------:|-------:|----------:|
| **PrefixReplayStreamCopyToNull**         | **262144**      |   **3,104.3 ns** |       **214.59 ns** |     **11.76 ns** | **0.0114** |      **-** |     **112 B** |
| PrefixReplayStreamReadPrefix         | 262144      |     680.5 ns |       195.22 ns |     10.70 ns | 0.0134 |      - |     112 B |
| PrefixReplayStreamReadAcrossBoundary | 262144      |   1,161.1 ns |       122.89 ns |      6.74 ns | 0.0134 |      - |     112 B |
| ComputeQuickHash                     | 262144      |   2,658.3 ns |       211.19 ns |     11.58 ns | 7.8735 |      - |   66320 B |
| ComputeQuickAndFullHash              | 262144      |  11,785.2 ns |     9,475.14 ns |    519.36 ns | 7.9956 | 1.9836 |   67248 B |
| ComputeQuickAndFullHashFromFile      | 262144      | 244,583.2 ns |   105,442.92 ns |  5,779.68 ns | 7.8125 |      - |   67434 B |
| **PrefixReplayStreamCopyToNull**         | **4194304**     |  **51,689.1 ns** |     **2,079.26 ns** |    **113.97 ns** |      **-** |      **-** |     **112 B** |
| PrefixReplayStreamReadPrefix         | 4194304     |   1,046.6 ns |        14.29 ns |      0.78 ns | 0.0134 |      - |     112 B |
| PrefixReplayStreamReadAcrossBoundary | 4194304     |   1,166.3 ns |        91.79 ns |      5.03 ns | 0.0134 |      - |     112 B |
| ComputeQuickHash                     | 4194304     |   2,975.7 ns |     5,343.24 ns |    292.88 ns | 7.8735 |      - |   66320 B |
| ComputeQuickAndFullHash              | 4194304     | 165,889.7 ns |   692,960.13 ns | 37,983.48 ns | 7.8125 | 1.7090 |   67248 B |
| ComputeQuickAndFullHashFromFile      | 4194304     | 639,976.1 ns | 1,443,704.24 ns | 79,134.30 ns | 7.8125 |      - |   67434 B |
