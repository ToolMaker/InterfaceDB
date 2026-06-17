```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.7417/22H2/2022Update)
AMD Ryzen 9 3950X 3.49GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  Job-CNUJVU : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3


```
| Method                                                    | Job        | InvocationCount | UnrollFactor | Mean       | Error     | StdDev    | Gen0       | Gen1      | Gen2      | Allocated    |
|---------------------------------------------------------- |----------- |---------------- |------------- |-----------:|----------:|----------:|-----------:|----------:|----------:|-------------:|
| &#39;Interface DB - 100 predicate scans over 50k persons&#39;     | DefaultJob | Default         | 16           | 2,082.8 ms |  38.02 ms |  35.56 ms |  2000.0000 | 2000.0000 | 2000.0000 |  78198.27 KB |
| &#39;SQL Server - 100 indexed count queries over 50k persons&#39; | DefaultJob | Default         | 16           |   111.0 ms |   0.71 ms |   0.66 ms |          - |         - |         - |    348.21 KB |
| &#39;Interface DB (transaction baseline) - store 50k persons&#39; | Job-CNUJVU | 1               | 1            | 5,482.1 ms | 104.35 ms | 115.98 ms |  5000.0000 | 1000.0000 |         - | 180127.45 KB |
| &#39;SQL Server (transaction) - store 50k persons&#39;            | Job-CNUJVU | 1               | 1            | 3,476.2 ms |  69.16 ms | 164.36 ms | 16000.0000 |         - |         - | 163676.52 KB |
| &#39;Dapper (transaction) - store 50k persons&#39;                | Job-CNUJVU | 1               | 1            | 3,668.0 ms |  73.17 ms | 131.94 ms | 47000.0000 |         - |         - | 414453.66 KB |
| &#39;SQL Server (SqlBulkCopy) - store 50k persons&#39;            | Job-CNUJVU | 1               | 1            |   181.5 ms |   2.42 ms |   2.27 ms |          - |         - |         - |  23661.09 KB |
