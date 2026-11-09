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
| Method                               | ContentSize | Mean       | Error         | StdDev      | Gen0   | Gen1   | Allocated |
|------------------------------------- |------------ |-----------:|--------------:|------------:|-------:|-------:|----------:|
| **PrefixReplayStreamCopyToNull**         | **262144**      |   **5.063 μs** |     **0.6296 μs** |   **0.0345 μs** | **0.0114** |      **-** |     **112 B** |
| PrefixReplayStreamReadPrefix         | 262144      |   1.734 μs |     0.6196 μs |   0.0340 μs | 0.0134 |      - |     112 B |
| PrefixReplayStreamReadAcrossBoundary | 262144      |   1.905 μs |     0.6890 μs |   0.0378 μs | 0.0134 |      - |     112 B |
| ComputeQuickHash                     | 262144      |   5.278 μs |     0.6295 μs |   0.0345 μs | 7.8735 |      - |   66320 B |
| ComputeQuickAndFullHash              | 262144      |  19.366 μs |     5.3531 μs |   0.2934 μs | 7.9956 | 1.9836 |   67248 B |
| ComputeQuickAndFullHashFromFile      | 262144      | 279.087 μs |   174.7999 μs |   9.5814 μs | 7.8125 |      - |   67434 B |
| **PrefixReplayStreamCopyToNull**         | **4194304**     |  **84.639 μs** |    **17.9563 μs** |   **0.9842 μs** |      **-** |      **-** |     **112 B** |
| PrefixReplayStreamReadPrefix         | 4194304     |   1.740 μs |     0.6197 μs |   0.0340 μs | 0.0134 |      - |     112 B |
| PrefixReplayStreamReadAcrossBoundary | 4194304     |   1.921 μs |     0.4717 μs |   0.0259 μs | 0.0134 |      - |     112 B |
| ComputeQuickHash                     | 4194304     |   5.254 μs |     0.6004 μs |   0.0329 μs | 7.8735 |      - |   66320 B |
| ComputeQuickAndFullHash              | 4194304     | 222.851 μs |    51.3011 μs |   2.8120 μs | 7.8125 | 1.7090 |   67248 B |
| ComputeQuickAndFullHashFromFile      | 4194304     | 909.950 μs | 3,845.1444 μs | 210.7653 μs | 7.8125 |      - |   67434 B |
