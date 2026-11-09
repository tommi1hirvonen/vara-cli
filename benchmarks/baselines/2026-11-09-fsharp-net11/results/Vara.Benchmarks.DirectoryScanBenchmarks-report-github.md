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
| Method           | FileCount | Mean      | Error      | StdDev    | Gen0     | Allocated  |
|----------------- |---------- |----------:|-----------:|----------:|---------:|-----------:|
| **ScanAndEnumerate** | **256**       |  **13.10 ms** |   **3.893 ms** |  **0.213 ms** |  **15.6250** |  **230.49 KB** |
| **ScanAndEnumerate** | **4096**      | **219.87 ms** | **760.514 ms** | **41.686 ms** | **333.3333** | **3233.49 KB** |
