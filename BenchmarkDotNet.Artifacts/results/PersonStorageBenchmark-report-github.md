```

BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.6456/22H2/2022Update)
AMD Ryzen 9 3950X, 1 CPU, 32 logical and 16 physical cores
.NET SDK 9.0.306
  [Host]     : .NET 9.0.10 (9.0.1025.47515), X64 RyuJIT AVX2
  Job-AQQYSU : .NET 9.0.10 (9.0.1025.47515), X64 RyuJIT AVX2

InvocationCount=1  UnrollFactor=1  

```
| Method                       | Mean     | Error    | StdDev   | Median   | Gen0      | Gen1      | Allocated |
|----------------------------- |---------:|---------:|---------:|---------:|----------:|----------:|----------:|
| &#39;File system repository&#39;     | 558.1 ms | 19.45 ms | 57.36 ms | 527.5 ms | 2000.0000 | 1000.0000 |  15.77 MB |
| &#39;SQL Server&#39;                 | 611.5 ms |  7.27 ms |  6.44 ms | 609.2 ms |         - |         - |   3.86 MB |
| &#39;Dapper - store 100 persons&#39; | 651.3 ms | 11.21 ms | 10.49 ms | 649.3 ms | 2000.0000 | 1000.0000 |  20.37 MB |
