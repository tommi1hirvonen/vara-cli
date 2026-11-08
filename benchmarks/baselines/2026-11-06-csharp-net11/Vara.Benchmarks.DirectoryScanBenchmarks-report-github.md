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
| Method           | FileCount | Mean      | Error     | StdDev   | Gen0     | Allocated  |
|----------------- |---------- |----------:|----------:|---------:|---------:|-----------:|
| **ScanAndEnumerate** | **256**       |  **10.86 ms** |  **7.021 ms** | **0.385 ms** |  **15.6250** |  **230.42 KB** |
| **ScanAndEnumerate** | **4096**      | **160.31 ms** | **19.142 ms** | **1.049 ms** | **250.0000** | **3293.42 KB** |
