# Current Performance Results

## Scope

These numbers are a reproducible development snapshot, not a universal product ranking. They show the current file provider, expose its strengths and costs, and provide a baseline for future changes.

- Date: 2026-08-23
- SDK: .NET 10.0.301
- Runtime used by the gate: .NET 10.0.9
- OS: Microsoft Windows 10 Pro 10.0.19045
- CPU: AMD Ryzen 9 3950X, 16 cores / 32 logical processors
- Storage: Samsung SSD 980 PRO 1 TB NVMe
- Records: 1,000
- Cache: InterfaceDB `StrongObject`; SQLite page cache
- Seed contract: one awaited object write per operation
- Durable comparison: InterfaceDB `Durable`; SQLite `synchronous=FULL`
- Query contract: return complete objects, materialize every result, then validate count and checksum
- Reopen contract: reconstruct the provider with the operating-system cache still warm
- InterfaceDB gate policy: single serialized write buffer, notifications/watcher disabled, hydration disabled, and concurrency bounded at 16

## Durable Results

| Operation | InterfaceDB | SQLite direct | SQLite + Dapper |
|---|---:|---:|---:|
| Independent object write, p50 | 5.626 ms | 3.019 ms | 3.094 ms |
| Independent object write, p95 | 6.332 ms | 3.211 ms | 3.274 ms |
| Exact query, 1 object, p50 | 0.342 ms | 0.025 ms | 0.030 ms |
| Range query, 90 objects, p50 | 0.161 ms | 0.212 ms | 0.185 ms |
| Prefix query, 1,000 objects, p50 | 1.326 ms | 1.754 ms | 1.505 ms |
| Suffix query, 2 objects, p50 | 1.014 ms | 0.125 ms | 0.124 ms |
| Contains query, 4 objects, p50 | 0.299 ms | 0.117 ms | 0.114 ms |
| Broad indexed conjunction, 324 objects, p50 | 0.465 ms | 0.628 ms | 0.554 ms |
| Warm reopen and index/catalog build | 304.289 ms | 8.241 ms | 6.673 ms |

Two-second mixed read/write stress at concurrency 2 completed with zero errors and matching final data:

| Engine | Operations/s | p50 | p95 | p99 | Integrity |
|---|---:|---:|---:|---:|---|
| InterfaceDB Durable | 707.1 | 0.770 ms | 10.388 ms | 13.416 ms | Pass |
| SQLite direct FULL | 754.3 | 0.436 ms | 3.205 ms | 7.424 ms | Pass |
| SQLite + Dapper FULL | 855.5 | 0.508 ms | 3.167 ms | 3.398 ms | Pass |

The forced-termination cycle recovered 18 committed records, left zero proposal artifacts, and passed final validation.

## Performance-Mode Results

InterfaceDB Performance mode measured a 0.400 ms median independent write and 2,347 records/s. SQLite NORMAL measured 0.077 ms direct and 0.073 ms through Dapper. Performance mode writes the canonical object file directly; it does not claim the same crash-recovery behavior as Durable mode.

## File-Provider Optimization Result

The earlier Durable implementation measured 15.299 ms p50 per independent object write. Profiling and a filesystem protocol probe identified redundant persistence work in ephemeral locks and an overlapping payload flush. The current implementation measures 5.626 ms p50:

| Durable file-provider revision | p50 | Relative throughput |
|---|---:|---:|
| Earlier protocol | 15.299 ms | 1.00x |
| Current protocol | 5.626 ms | 2.72x |

The optimized protocol still passes cross-process write/delete ordering, abandoned proposal recovery, terminated-process recovery, migration coordination, encryption rotation, and the full integration harness. Per-identity semaphores are also retired after use instead of accumulating for every logical key.

The remaining durable write gap is primarily structural: InterfaceDB creates and publishes a separately recoverable file for every object, while SQLite reuses a WAL/page architecture. Removing the remaining writer lease, sidecar lock, or two-phase proposal publication would weaken a tested guarantee unless replaced by another coordination mechanism. An append-only provider is therefore the next architecture experiment rather than another blind micro-optimization of the file-per-object provider.

## Reading The Results Correctly

- Exact, suffix, and contains lookups have small result sets, so InterfaceDB's expression planning and candidate materialization overhead is visible.
- Range, broad conjunction, and broad prefix results amortize that fixed cost and can be competitive with the measured SQLite object materialization paths.
- InterfaceDB rebuilds in-memory indexes from object files when opening. SQLite reopens persistent B-tree indexes, so reopen time is not close.
- Strong-object results describe a hot object cache. Metadata-only mode includes file reads and deserialization and must be measured separately for cold/local-storage workloads.
- “End to end” means from each public storage/query API through complete C# object materialization and validation. It does not claim a physical-media read when an OS page cache or InterfaceDB object cache satisfies the request.
- A 1,000-record quick gate is a regression and correctness signal. Public release claims should use the documented 10k/100k soak matrix on multiple machines and platforms.

## Reproduce

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick `
  -Output TestResults\production-gate-quick.json
```

Generated JSON, Markdown, traces, and BenchmarkDotNet output are ignored by Git. Promote only reviewed results into this document and record the environment, modes, workload, and object-materialization contract.
