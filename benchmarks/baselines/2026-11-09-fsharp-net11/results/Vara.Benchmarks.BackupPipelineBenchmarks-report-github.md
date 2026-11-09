```

BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 9 HX PRO 375 w/ Radeon 890M 2.00GHz, 1 CPU, 24 logical and 12 physical cores
Memory: 63,28 GB Total, 38,28 GB Available
.NET SDK 11.0.100-rc.3.26522.148
  [Host]   : .NET 11.0.0 (11.0.0-rc.3.26522.148, 11.0.26.52348), X64 RyuJIT x86-64-v4 DEBUG
  ShortRun : .NET 11.0.0 (11.0.0-rc.3.26522.148, 11.0.26.52348), X64 RyuJIT x86-64-v4

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method         | FileCount | BytesPerFile | Mean      | Error     | StdDev   | Gen0      | Allocated |
|--------------- |---------- |------------- |----------:|----------:|---------:|----------:|----------:|
| **RunFirstBackup** | **8**         | **65536**        |  **54.21 ms** |  **79.35 ms** | **4.350 ms** |         **-** |   **1.27 MB** |
| **RunFirstBackup** | **8**         | **1048576**      |  **51.97 ms** |  **10.40 ms** | **0.570 ms** |         **-** |   **1.27 MB** |
| **RunFirstBackup** | **64**        | **65536**        | **134.03 ms** | **165.15 ms** | **9.052 ms** | **1000.0000** |   **9.88 MB** |
| **RunFirstBackup** | **64**        | **1048576**      | **163.69 ms** | **159.99 ms** | **8.770 ms** | **1000.0000** |   **9.88 MB** |
