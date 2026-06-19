```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.7417/22H2/2022Update)
AMD Ryzen 9 3950X 3.49GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  Job-CNUJVU : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3


```
| Method                                                                                                      | Job        | InvocationCount | UnrollFactor | Mean        | Error      | StdDev     | Median      | Gen0       | Gen1      | Gen2      | Allocated |
|------------------------------------------------------------------------------------------------------------ |----------- |---------------- |------------- |------------:|-----------:|-----------:|------------:|-----------:|----------:|----------:|----------:|
| &#39;InterfaceDB FileEnvelopeRepository + PassthroughIndexManager - 100 predicate scans over 50k persons&#39;       | DefaultJob | Default         | 16           | 1,320.54 ms |  46.084 ms | 135.880 ms | 1,328.05 ms |          - |         - |         - |   38.2 MB |
| &#39;SQL Server ADO.NET + SQL indexes - 100 materialized queries over 50k persons&#39;                              | DefaultJob | Default         | 16           | 1,053.77 ms |  13.809 ms |  12.917 ms | 1,055.83 ms |  9000.0000 | 5000.0000 | 3000.0000 | 266.19 MB |
| &#39;Dapper SQL Server + SQL indexes - 100 materialized queries over 50k persons&#39;                               | DefaultJob | Default         | 16           | 1,173.13 ms |  20.863 ms |  28.558 ms | 1,166.22 ms | 10000.0000 | 5000.0000 | 3000.0000 | 349.81 MB |
| &#39;Entity Framework Core SQL Server + SQL indexes - 100 materialized queries over 50k persons&#39;                | DefaultJob | Default         | 16           | 1,368.51 ms |  27.364 ms |  76.730 ms | 1,363.70 ms | 15000.0000 | 6000.0000 | 4000.0000 | 563.22 MB |
| &#39;InterfaceDB FileEnvelopeRepository + SearchIndexManager - 100 indexed searches over 50k persons&#39;           | DefaultJob | Default         | 16           |   880.51 ms |  16.956 ms |  34.253 ms |   864.77 ms |  1000.0000 |         - |         - |  84.18 MB |
| &#39;InterfaceDB FileEnvelopeRepository + SearchIndexManager - 100 selective indexed searches over 50k persons&#39; | DefaultJob | Default         | 16           |    64.49 ms |   1.152 ms |   1.183 ms |    64.18 ms |  1000.0000 |  500.0000 |         - |   8.41 MB |
| &#39;SQL Server ADO.NET + SQL indexes - 100 selective materialized queries over 50k persons&#39;                    | DefaultJob | Default         | 16           |   161.59 ms |   0.541 ms |   0.479 ms |   161.44 ms |  1250.0000 |  250.0000 |         - |  10.63 MB |
| &#39;Dapper SQL Server + SQL indexes - 100 selective materialized queries over 50k persons&#39;                     | DefaultJob | Default         | 16           |   164.82 ms |   1.278 ms |   1.196 ms |   164.57 ms |  1666.6667 |  333.3333 |         - |  13.84 MB |
| &#39;Entity Framework Core SQL Server + SQL indexes - 100 selective materialized queries over 50k persons&#39;      | DefaultJob | Default         | 16           |   184.85 ms |   2.499 ms |   2.337 ms |   185.50 ms |  3333.3333 | 1000.0000 |         - |   29.1 MB |
| &#39;InterfaceDB FileEnvelopeRepository + PassthroughIndexManager - store 50k persons&#39;                          | Job-CNUJVU | 1               | 1            | 5,240.45 ms | 103.265 ms | 141.351 ms | 5,205.16 ms |  5000.0000 | 1000.0000 |         - | 230.43 MB |
| &#39;InterfaceDB FileEnvelopeRepository + SearchIndexManager - store 50k persons&#39;                               | Job-CNUJVU | 1               | 1            | 5,533.27 ms | 110.223 ms |  97.710 ms | 5,517.18 ms |  6000.0000 | 1000.0000 |         - | 325.46 MB |
| &#39;SQL Server (object transaction) - store 50k persons&#39;                                                       | Job-CNUJVU | 1               | 1            | 3,796.24 ms |  75.517 ms | 165.762 ms | 3,797.93 ms | 16000.0000 |         - |         - | 159.84 MB |
| &#39;SQL Server (indexed object transaction) - store 50k persons&#39;                                               | Job-CNUJVU | 1               | 1            | 4,183.05 ms |  83.078 ms | 105.067 ms | 4,227.39 ms | 16000.0000 |         - |         - | 159.84 MB |
| &#39;Dapper (object transaction) - store 50k persons&#39;                                                           | Job-CNUJVU | 1               | 1            | 3,980.96 ms |  77.546 ms | 116.067 ms | 3,942.55 ms | 47000.0000 |         - |         - | 404.74 MB |
| &#39;Dapper (indexed object transaction) - store 50k persons&#39;                                                   | Job-CNUJVU | 1               | 1            | 4,409.31 ms |  85.179 ms | 204.084 ms | 4,423.73 ms | 47000.0000 |         - |         - | 404.74 MB |
| &#39;Entity Framework Core SQL Server - store 50k persons&#39;                                                      | Job-CNUJVU | 1               | 1            | 1,682.89 ms |  33.551 ms |  47.034 ms | 1,666.68 ms |  6000.0000 | 2000.0000 | 1000.0000 |  355.9 MB |
| &#39;Entity Framework Core SQL Server + SQL indexes - store 50k persons&#39;                                        | Job-CNUJVU | 1               | 1            | 1,896.17 ms |  22.350 ms |  19.813 ms | 1,895.72 ms |  6000.0000 | 2000.0000 | 1000.0000 |  355.9 MB |
| &#39;SQL Server (SqlBulkCopy) - store 50k persons&#39;                                                              | Job-CNUJVU | 1               | 1            |   179.34 ms |   1.266 ms |   1.123 ms |   179.16 ms |          - |         - |         - |  23.11 MB |
