```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.7417/22H2/2022Update)
AMD Ryzen 9 3950X 3.49GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  Job-CNUJVU : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

InvocationCount=1  UnrollFactor=1  

```
| Method                                                    | Mean    | Error    | StdDev   | Gen0      | Gen1      | Allocated |
|---------------------------------------------------------- |--------:|---------:|---------:|----------:|----------:|----------:|
| &#39;Interface DB (transaction baseline) - store 50k persons&#39; | 5.461 s | 0.1061 s | 0.1487 s | 4000.0000 | 1000.0000 | 164.32 MB |
