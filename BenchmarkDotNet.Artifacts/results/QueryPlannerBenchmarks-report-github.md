```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.7663/22H2/2022Update)
AMD Ryzen 9 3950X 3.49GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.301
  [Host]   : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                                                            | RecordCount | Mean        | Error        | StdDev     | Ratio | RatioSD | Gen0    | Gen1    | Gen2    | Allocated | Alloc Ratio |
|------------------------------------------------------------------ |------------ |------------:|-------------:|-----------:|------:|--------:|--------:|--------:|--------:|----------:|------------:|
| &#39;Full scan: Group == A &amp;&amp; Age &gt;= midpoint&#39;                        | 100000      |   302.72 μs |   376.427 μs |  20.633 μs |  1.00 |    0.08 |       - |       - |       - |         - |          NA |
| &#39;Previous planner: smallest single index plus residual filtering&#39; | 100000      | 2,426.34 μs | 2,477.215 μs | 135.785 μs |  8.04 |    0.60 | 15.6250 | 15.6250 | 15.6250 | 1509285 B |          NA |
| &#39;Always intersect every usable index&#39;                             | 100000      | 5,480.59 μs | 1,861.822 μs | 102.053 μs | 18.16 |    1.07 | 31.2500 | 31.2500 | 31.2500 | 2557760 B |          NA |
| &#39;Cost-aware conditional index intersection&#39;                       | 100000      | 2,503.28 μs | 2,836.865 μs | 155.498 μs |  8.29 |    0.65 | 15.6250 | 15.6250 | 15.6250 | 1510442 B |          NA |
| &#39;Full ordinal scan: rare contains&#39;                                | 100000      | 3,173.83 μs |   384.443 μs |  21.073 μs | 10.52 |    0.60 |       - |       - |       - |         - |          NA |
| &#39;Adaptive gram access path: rare contains&#39;                        | 100000      |    44.36 μs |     1.254 μs |   0.069 μs |  0.15 |    0.01 |  0.5493 |       - |       - |    4897 B |          NA |
