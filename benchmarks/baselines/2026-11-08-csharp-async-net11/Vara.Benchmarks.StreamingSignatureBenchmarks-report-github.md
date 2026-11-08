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
| Method                               | ContentSize | Mean         | Error         | StdDev      | Gen0   | Gen1   | Allocated |
|------------------------------------- |------------ |-------------:|--------------:|------------:|-------:|-------:|----------:|
| **PrefixReplayStreamCopyToNull**         | **262144**      |   **3,094.1 ns** |      **95.32 ns** |     **5.23 ns** | **0.0114** |      **-** |     **112 B** |
| PrefixReplayStreamReadPrefix         | 262144      |   1,035.9 ns |      44.67 ns |     2.45 ns | 0.0134 |      - |     112 B |
| PrefixReplayStreamReadAcrossBoundary | 262144      |   1,124.8 ns |      24.45 ns |     1.34 ns | 0.0134 |      - |     112 B |
| ComputeQuickHash                     | 262144      |   2,726.3 ns |     284.83 ns |    15.61 ns | 7.8735 | 0.0038 |   66416 B |
| ComputeQuickAndFullHash              | 262144      |  11,440.0 ns |   2,670.16 ns |   146.36 ns | 7.9956 |      - |   67160 B |
| ComputeQuickAndFullHashFromFile      | 262144      | 192,890.2 ns | 153,938.46 ns | 8,437.89 ns | 7.8125 | 1.4648 |   67418 B |
| **PrefixReplayStreamCopyToNull**         | **4194304**     |  **52,507.1 ns** |   **1,458.16 ns** |    **79.93 ns** |      **-** |      **-** |     **112 B** |
| PrefixReplayStreamReadPrefix         | 4194304     |     691.5 ns |      93.16 ns |     5.11 ns | 0.0134 |      - |     112 B |
| PrefixReplayStreamReadAcrossBoundary | 4194304     |   1,116.0 ns |      13.63 ns |     0.75 ns | 0.0134 |      - |     112 B |
| ComputeQuickHash                     | 4194304     |   2,761.4 ns |      79.76 ns |     4.37 ns | 7.8735 | 0.0038 |   66416 B |
| ComputeQuickAndFullHash              | 4194304     | 141,730.1 ns |  11,924.86 ns |   653.64 ns | 7.8125 |      - |   67160 B |
| ComputeQuickAndFullHashFromFile      | 4194304     | 581,092.4 ns | 158,673.31 ns | 8,697.42 ns | 7.8125 | 0.9766 |   67418 B |
