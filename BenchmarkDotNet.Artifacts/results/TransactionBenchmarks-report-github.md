```

BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.6332/22H2/2022Update)
AMD Ryzen 9 3950X, 1 CPU, 32 logical and 16 physical cores
.NET SDK 9.0.306
  [Host]     : .NET 9.0.10 (9.0.1025.47515), X64 RyuJIT AVX2
  Job-AVRYPF : .NET 9.0.10 (9.0.1025.47515), X64 RyuJIT AVX2

InvocationCount=1  UnrollFactor=1  

```
| Method                                                    | Mean    | Error    | StdDev   | Median  | Gen0       | Gen1      | Allocated |
|---------------------------------------------------------- |--------:|---------:|---------:|--------:|-----------:|----------:|----------:|
| &#39;Interface DB (transaction baseline) - store 50k persons&#39; | 9.250 s | 0.1796 s | 0.2271 s | 9.250 s | 17000.0000 | 6000.0000 | 135.98 MB |
| &#39;SQL Server (transaction) - store 50k persons&#39;            | 3.039 s | 0.0930 s | 0.2654 s | 3.024 s |  4000.0000 | 1000.0000 |  38.53 MB |
| &#39;Dapper (transaction) - store 50k persons&#39;                | 3.498 s | 0.0832 s | 0.2374 s | 3.414 s | 17000.0000 | 1000.0000 | 136.95 MB |
