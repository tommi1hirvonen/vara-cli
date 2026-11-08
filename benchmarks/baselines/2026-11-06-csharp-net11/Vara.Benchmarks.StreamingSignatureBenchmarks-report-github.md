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
| Method                               | ContentSize | Mean         | Error           | StdDev       | Gen0   | Gen1   | Allocated |
|------------------------------------- |------------ |-------------:|----------------:|-------------:|-------:|-------:|----------:|
| **PrefixReplayStreamCopyToNull**         | **262144**      |   **4,566.4 ns** |    **18,079.43 ns** |    **990.99 ns** | **0.0114** |      **-** |     **112 B** |
| PrefixReplayStreamReadPrefix         | 262144      |   1,727.3 ns |       345.66 ns |     18.95 ns | 0.0134 |      - |     112 B |
| PrefixReplayStreamReadAcrossBoundary | 262144      |   1,455.9 ns |     6,983.01 ns |    382.76 ns | 0.0134 |      - |     112 B |
| ComputeQuickHash                     | 262144      |   2,727.1 ns |        30.63 ns |      1.68 ns | 7.8735 | 0.0038 |   66416 B |
| ComputeQuickAndFullHash              | 262144      |  11,353.6 ns |     1,522.37 ns |     83.45 ns | 7.9956 |      - |   67160 B |
| ComputeQuickAndFullHashFromFile      | 262144      | 226,945.7 ns |     9,806.06 ns |    537.50 ns | 7.8125 | 1.4648 |   67418 B |
| **PrefixReplayStreamCopyToNull**         | **4194304**     |  **66,631.2 ns** |    **12,165.46 ns** |    **666.83 ns** |      **-** |      **-** |     **112 B** |
| PrefixReplayStreamReadPrefix         | 4194304     |   1,055.4 ns |       180.13 ns |      9.87 ns | 0.0134 |      - |     112 B |
| PrefixReplayStreamReadAcrossBoundary | 4194304     |     694.0 ns |       209.38 ns |     11.48 ns | 0.0134 |      - |     112 B |
| ComputeQuickHash                     | 4194304     |   2,929.7 ns |       460.11 ns |     25.22 ns | 7.8735 | 0.0038 |   66416 B |
| ComputeQuickAndFullHash              | 4194304     | 151,025.1 ns |    14,762.04 ns |    809.16 ns | 7.8125 |      - |   67160 B |
| ComputeQuickAndFullHashFromFile      | 4194304     | 943,694.1 ns | 1,136,276.04 ns | 62,283.12 ns | 7.8125 | 0.9766 |   67418 B |
