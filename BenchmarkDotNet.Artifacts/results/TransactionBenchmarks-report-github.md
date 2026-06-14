```

BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.6937/22H2/2022Update)
AMD Ryzen 9 3950X, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.102
  [Host]     : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2
  Job-ULKJYT : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2

InvocationCount=1  UnrollFactor=1  

```
| Method                                                    | Mean    | Error    | StdDev   | Gen0        | Gen1       | Gen2      | Allocated |
|---------------------------------------------------------- |--------:|---------:|---------:|------------:|-----------:|----------:|----------:|
| &#39;Interface DB (transaction baseline) - store 50k persons&#39; | 4.283 s | 0.0830 s | 0.1050 s | 102000.0000 | 71000.0000 | 1000.0000 | 483.65 MB |
| &#39;SQL Server (transaction) - store 50k persons&#39;            | 3.376 s | 0.0674 s | 0.1810 s |   4000.0000 |  1000.0000 |         - |  38.53 MB |
| &#39;Dapper (transaction) - store 50k persons&#39;                | 3.636 s | 0.0690 s | 0.1226 s |  17000.0000 |  1000.0000 |         - | 136.95 MB |
