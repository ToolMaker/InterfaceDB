```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.7417/22H2/2022Update)
AMD Ryzen 9 3950X 3.49GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  Job-CNUJVU : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3


```
| Method                                                                     | Job        | InvocationCount | UnrollFactor | Mean        | Error      | StdDev     | Gen0       | Gen1      | Gen2      | Allocated |
|--------------------------------------------------------------------------- |----------- |---------------- |------------- |------------:|-----------:|-----------:|-----------:|----------:|----------:|----------:|
| &#39;Interface DB - 100 predicate scans over 50k persons&#39;                      | DefaultJob | Default         | 16           | 1,069.74 ms |  15.977 ms |  14.945 ms |          - |         - |         - |   38.2 MB |
| &#39;Interface DB - 100 indexed searches over 50k persons&#39;                     | DefaultJob | Default         | 16           |   841.55 ms |  14.988 ms |  13.287 ms |  1000.0000 |         - |         - |  82.26 MB |
| &#39;Interface DB - 100 selective indexed searches over 50k persons&#39;           | DefaultJob | Default         | 16           |    63.19 ms |   1.167 ms |   1.433 ms |  1000.0000 |  428.5714 |         - |   8.41 MB |
| &#39;SQL Server - 100 indexed materialized queries over 50k persons&#39;           | DefaultJob | Default         | 16           |   912.15 ms |  11.702 ms |  10.946 ms |  4000.0000 | 3000.0000 | 2000.0000 | 265.33 MB |
| &#39;SQL Server - 100 selective indexed materialized queries over 50k persons&#39; | DefaultJob | Default         | 16           |   163.48 ms |   2.017 ms |   1.887 ms |  1250.0000 |  250.0000 |         - |  10.78 MB |
| &#39;Dapper - 100 indexed materialized queries over 50k persons&#39;               | DefaultJob | Default         | 16           | 1,016.84 ms |  19.263 ms |  18.019 ms |  4000.0000 | 3000.0000 | 2000.0000 | 349.28 MB |
| &#39;Dapper - 100 selective indexed materialized queries over 50k persons&#39;     | DefaultJob | Default         | 16           |   161.03 ms |   1.180 ms |   1.104 ms |  1750.0000 |  500.0000 |         - |  14.14 MB |
| &#39;Interface DB (transaction baseline) - store 50k persons&#39;                  | Job-CNUJVU | 1               | 1            | 5,491.32 ms | 107.020 ms | 184.605 ms |  4000.0000 |         - |         - | 227.36 MB |
| &#39;Interface DB (indexed transaction) - store 50k persons&#39;                   | Job-CNUJVU | 1               | 1            | 5,812.01 ms | 116.152 ms | 180.835 ms |  7000.0000 | 2000.0000 |         - | 343.21 MB |
| &#39;SQL Server (object transaction) - store 50k persons&#39;                      | Job-CNUJVU | 1               | 1            | 3,728.50 ms |  73.705 ms | 169.350 ms | 16000.0000 |         - |         - | 159.84 MB |
| &#39;SQL Server (indexed object transaction) - store 50k persons&#39;              | Job-CNUJVU | 1               | 1            | 4,159.31 ms |  82.799 ms | 215.206 ms | 16000.0000 |         - |         - | 159.84 MB |
| &#39;Dapper (object transaction) - store 50k persons&#39;                          | Job-CNUJVU | 1               | 1            | 3,912.45 ms |  95.593 ms | 281.857 ms | 47000.0000 |         - |         - | 404.74 MB |
| &#39;Dapper (indexed object transaction) - store 50k persons&#39;                  | Job-CNUJVU | 1               | 1            | 4,377.70 ms |  86.729 ms | 118.715 ms | 47000.0000 |         - |         - | 404.74 MB |
| &#39;SQL Server (SqlBulkCopy) - store 50k persons&#39;                             | Job-CNUJVU | 1               | 1            |   177.05 ms |   0.704 ms |   0.588 ms |          - |         - |         - |  23.11 MB |
