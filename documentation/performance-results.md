# Current Performance Results

## Scope

These numbers are reproducible development snapshots, not universal product rankings. The first gate covers the file provider; the SegmentCore section covers the append-only provider through the same complete-object comparison contract.

- Date: 2026-08-26
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
| Independent object write, p50 | 7.060 ms | 3.381 ms | 3.330 ms |
| Independent object write, p95 | 8.472 ms | 4.066 ms | 3.851 ms |
| Exact query, 1 object, p50 | 0.401 ms | 0.027 ms | 0.030 ms |
| Range query, 90 objects, p50 | 0.193 ms | 0.234 ms | 0.252 ms |
| Prefix query, 1,000 objects, p50 | 2.030 ms | 2.141 ms | 2.330 ms |
| Suffix query, 2 objects, p50 | 1.052 ms | 0.128 ms | 0.164 ms |
| Contains query, 4 objects, p50 | 0.405 ms | 0.120 ms | 0.145 ms |
| Broad indexed conjunction, 324 objects, p50 | 0.979 ms | 0.664 ms | 0.625 ms |
| Warm reopen and index/catalog build | 266.723 ms | 9.663 ms | 8.641 ms |

Two-second mixed read/write stress at concurrency 2 completed with zero errors and matching final data:

| Engine | Operations/s | p50 | p95 | p99 | Integrity |
|---|---:|---:|---:|---:|---|
| InterfaceDB Durable | 575.1 | 1.580 ms | 10.690 ms | 12.770 ms | Pass |
| SQLite direct FULL | 744.8 | 0.440 ms | 3.660 ms | 6.980 ms | Pass |
| SQLite + Dapper FULL | 680.3 | 0.640 ms | 3.620 ms | 6.230 ms | Pass |

The forced-termination cycle recovered 20 committed records, left zero proposal artifacts, and passed final validation.

## Performance-Mode Results

The current gate measured InterfaceDB Performance mode at 0.499 ms median and 1,697 independent writes/s. SQLite NORMAL measured 0.099 ms direct and 0.087 ms through Dapper. Two-second mixed stress reached 1,745 operations/s for InterfaceDB, 2,057/s for SQLite direct, and 2,183/s through Dapper; all integrity checks passed. Performance mode writes the canonical object file directly and does not claim the same crash-recovery behavior as Durable mode.

## File-Provider Optimization Result

The earlier Durable implementation measured 15.299 ms p50 per independent object write. Profiling and a filesystem protocol probe identified redundant persistence work in ephemeral locks and an overlapping payload flush. The current release-gate rerun measured 7.060 ms p50:

| Durable file-provider revision | p50 | Relative throughput |
|---|---:|---:|
| Earlier protocol | 15.299 ms | 1.00x |
| Current protocol | 7.060 ms | 2.17x |

The optimized protocol still passes cross-process write/delete ordering, abandoned proposal recovery, terminated-process recovery, migration coordination, encryption rotation, and the full integration harness. Per-identity semaphores are retired after use instead of accumulating for every logical key. File discovery no longer schedules one thread-pool yield per object, and the default identity factory now uses the .NET 10 vectorized `SearchValues<char>` path so an already-valid file key is returned without a second string allocation.

The remaining durable write gap is primarily structural: InterfaceDB creates and publishes a separately recoverable file for every object, while SQLite reuses a WAL/page architecture. Removing the remaining writer lease, sidecar lock, or two-phase proposal publication would weaken a tested guarantee unless replaced by another coordination mechanism. An append-only provider is therefore the next architecture experiment rather than another blind micro-optimization of the file-per-object provider.

## SegmentCore Production-Candidate Result

The release-candidate SegmentCore pass uses .NET 10, 5,000 complete six-field objects, five fresh stores, eight stripes, mutation-WAL transactions, and SQLite WAL with `synchronous=FULL`. Both engines encode/bind objects, durably acknowledge writes, reopen, materialize every complete object, and validate count/checksum.

| Atomic batch | SegmentCore explicit flush | SQLite | SegmentCore Windows write-through | SQLite |
|---:|---:|---:|---:|---:|
| 1 | 296 records/s | 292 records/s | 2,607 records/s | 301 records/s |
| 10 | 2,930 records/s | 2,920 records/s | 15,885 records/s | 2,900 records/s |
| 100 | 27,553 records/s | 26,191 records/s | 99,910 records/s | 24,198 records/s |
| 1,000 | 166,914 records/s | 137,569 records/s | 303,783 records/s | 136,790 records/s |
| Independent writes | 301 records/s | 297 records/s | 4,368 records/s | 300 records/s |

Portable explicit flush was 1.00x-1.21x faster before maintenance. After amortizing one checkpoint across 5,000 objects it remained 1.01x faster at batch 1, was effectively tied at batches 10 and 100, and SQLite led by 1.18x at batch 1,000. Qualified Windows write-through led by 2.22x-8.67x for atomic batches and 14.54x for independent writes before maintenance on this NVMe machine. It is not the portable default.

Mutation-WAL checkpoints measured 15.4-20.9 ms under explicit flush and 9.7-14.5 ms under write-through for the 5,000-object atomic workloads. SegmentCore reopened in 5.0-13.0 ms versus SQLite's 0.36-0.55 ms. Complete-object materialization remained workload-dependent. Missing or corrupt retained-WAL seek sidecars still fall back to complete WAL validation.

With 5,000 live objects and two obsolete update generations, bounded compaction reclaimed 2,154.9 KiB, reducing 6,740.4 KiB to 4,585.5 KiB. Stripe-by-stripe preparation, 64-record copy slices, and checkpoint publication outside the writer barrier limited the longest observed exclusive pause to 14.88 ms under explicit flush and 8.79 ms under Windows write-through. Total maintenance was 178.25 ms and 148.84 ms respectively across 89 short slices.

The provider passes 93 dedicated tests on Windows and Linux. Forced process termination after durable commit and on both sides of WAL replacement recovered every committed object in both final transaction runs. Linux additionally exposed and now regression-tests a fresh-segment combined-batch rotation defect: recovery was intact, but a large first batch could exceed the configured segment bound. See [SegmentCore Storage Provider](segment-core-storage-provider.md) for operations, limitations, and raw report names.

### Repository-Level Production Pilot

The raw transaction comparison above isolates the physical engine. A separate maintained pilot goes
through `IRepository<T>`, MessagePack envelopes, the metadata cache, and persisted exact/range/prefix/
suffix/contains indexes. With 10,000 initial objects and five update/insert/delete cycles it ended
with 10,500 objects and passed every authoritative object/checksum comparison, every indexed query
family on every cycle, checkpoint, integrity, compaction, online backup, warm reopen, restore, and
index-catalog rebuild.

| Repository operation | Current result |
|---|---:|
| Atomic indexed seed | 13,575 objects/s |
| Atomic tracked-object updates | 7,483-19,565 objects/s |
| Atomic inserts | 5,388-27,249 objects/s |
| Indexed object-at-a-time deletes | 204-286 objects/s |
| Compaction | 263.85 ms; 5,611,978 B reclaimed; 18.87 ms longest exclusive slice |
| Verified online backup | 373.73 ms for 4,399,000 B |
| Repository reopen with persisted indexes | 557.05 ms |
| Restored repository open/catalog rebuild | 357.55 ms |

This is an application-readiness regression gate, not a SQLite ranking. It also codifies identity
semantics: multiple-cardinality updates use loaded/tracked objects. A detached object is an insert,
not an implicit replacement.

### Final Scale And Allocation Gates

The current write-through gate uses 256-byte deterministic payloads, 1,000-object atomic batches,
16 producers, eight stripes, a full primary checkpoint, an immediate unchanged checkpoint,
streaming integrity verification of every live frame, checkpoint reopen with zero tail replay, and
10,000 complete sampled point reads.

| 1,000,000 objects | Ingestion | Full checkpoint | No-op checkpoint | Streaming integrity | Reopen / allocation | Sampled point reads |
|---|---:|---:|---:|---:|---:|---:|
| Resident locator | 286,655 objects/s | 653.2 ms | 1.5 ms / 1,728 B | 403,335 objects/s | 927.2 ms / 139.9 MiB | 131,930 reads/s |
| Paged locator | 278,641 objects/s | 915.2 ms | 1.4 ms / 1,728 B | 408,379 objects/s | 42.6 ms / 25.2 MiB | 79,774 reads/s |

Both modes passed count, checksum, full-payload, checkpoint-generation, and zero-tail-replay
assertions. Paged reopen was 21.8x faster and allocated 82.0% less, at the cost of a 40.1% slower full
checkpoint and 39.5% lower sampled random-read throughput. Automatic mode selects paging from
100,000 live entries. The repository adapter still materializes envelope key skeletons even when
the underlying raw locator is paged.

### IL/JIT And Allocation Audit

The .NET 10 full-optimization JIT output for `Crc32C.Append` is already a 212-byte hardware-intrinsic loop using 64-bit, 32-bit, and byte-tail `crc32` instructions. `Span` and `BinaryPrimitives` operations are inlined and the hot loop contains no managed calls, so handwritten IL would add maintenance risk without a faster instruction shape. A fused two-checksum prototype was 3.6% slower and was reverted. A custom eight-pass radix sort also failed to beat warmed `Array.Sort` reliably and was not shipped.

Measured managed changes that did earn their place are:

- zero-allocation ASCII key validation plus cached segment paths/handles raised fresh-process point reads from 55,209/s to 62,166/s and cut read-path allocation from 33.1 MB to 8.4 MB for 25,000 reads;
- direct primitive checkpoint arrays and streamed page-map validation cut one-million-entry paged reopen allocation from 72.4 MB to roughly 24-26 MB;
- validating the authoritative checkpoint and derived page map in parallel reduced repeated warm paged reopen from roughly 45.8 ms to 26.4 ms; the final cold gate measured 42.6 ms;
- publishing a large page map in parallel with the authoritative checkpoint reduced the three-run checkpoint median from 1,009.5 ms to 962.6 ms (4.65%);
- increasing the fully validating WAL SHA-256 stream buffer from 64 KiB to 1 MiB reduced the matched 268 MB-prefix reopen median from 215.7 ms to 209.8 ms (2.7%), while preserving full-prefix verification;
- the scale validator itself previously allocated an interpolated failure message for every valid payload byte. Correcting that benchmark defect changed 64 sampled 1 MiB reads from 6.83 s/7.60 GB allocated to 85.9 ms/67.2 MB before subsequent reader improvements. This was a benchmark correction, not a claimed engine speedup.

An O(1) retained-WAL shortcut was deliberately not implemented because it would stop byte-for-byte verification of checkpoint-covered WAL history. That recovery-integrity trade-off requires an explicit product decision rather than a hidden performance patch.

### Release Qualification Snapshot

- A genuine larger-than-memory run stored and integrity-checked 34,000 independent 1 MiB objects: 35.65 GB of live payload versus a 34.26 GB managed-memory budget (1.041x). It ingested at 143.9 objects/s, verified at 989.1 objects/s, reopened in 932.8 ms with a fully verified 755 MB retained WAL prefix, and sampled 1 MiB objects at 502.5 reads/s.
- A five-minute Windows run completed 81,473,282 mixed operations with concurrent writes, four readers, one-second checkpoints, exact model comparison, full integrity verification, 72.99 ms reopen, and passing injected disk-exhaustion recovery.
- The latest .NET 10 Linux container passed all 93 SegmentCore tests, all 20 direct InterfaceDB unit tests, and all 49 integration scenarios. Its maintained full Phase 0 gate passed 12/12 forced-termination cycles but failed the performance admission threshold: explicit flush was only 5.4% faster than SQLite direct at p50, below the required 15%. The current three-cycle quick rerun also passed correctness but led by only 11.1%. This is a disclosed loser and remains virtualized evidence, not bare-metal certification.
- The current 30-second Windows mixed-load gate completed 12,641,604 operations, reopened in 17.91 ms, and passed injected disk-exhaustion recovery.
- A locally retained package `1.0.0-preview.2` baseline to current-source upgrade validation passed open/read/mutate/checkpoint/integrity/backup/restore/reopen, and freshly packed packages passed an isolated clean-consumer round trip.
- A fresh-process point-read comparison at 100,000 objects measured SegmentCore at 64,624 reads/s versus SQLite at 31,182/s, but SegmentCore opened in 133.13 ms versus SQLite at 22.75 ms.
- Four concurrent processes reading one SQLite store all succeeded. SegmentCore supports one owning process per live store today; only one of four Windows workers and zero of four simultaneously racing Linux workers opened the same store. This is a measured product boundary, not a hidden qualification pass.
- Physical process-kill recovery passes, but actual removal of system power and a post-reboot cold-media measurement still require dedicated hardware.

### Current Measured Losers

| Workload | InterfaceDB / SegmentCore | Best SQLite result | Current gap |
|---|---:|---:|---:|
| File provider, durable independent write p50 | 7.060 ms | 3.330 ms | 2.12x slower |
| File provider, warm indexed reopen | 266.723 ms | 8.641 ms | 30.9x slower |
| File provider, exact query p50 | 0.401 ms | 0.027 ms | 14.9x slower |
| File provider, suffix query p50 | 1.052 ms | 0.128 ms | 8.22x slower |
| File provider, contains query p50 | 0.405 ms | 0.120 ms | 3.38x slower |
| File provider, performance-mode write p50 | 0.499 ms | 0.087 ms | 5.74x slower |
| SegmentCore explicit batch 1,000 plus checkpoint | 98,420 records/s | 116,250 records/s | 1.18x slower |
| SegmentCore fresh-process open, 100,000 objects | 133.13 ms | 22.75 ms | 5.85x slower |
| SegmentCore post-checkpoint footprint, batch 1,000 | 2,384,636 B | 733,184 B | 3.25x larger |
| Quick adversarial space reclamation | 104.78 ms | 21.78 ms SQLite VACUUM | 4.81x slower |
| Concurrent processes on one live store | 1/4 opened | 4/4 opened | Unsupported boundary |
| Virtualized-Linux strict p50 admission | 5.4% lead | 15% required | Gate failed |

### Current Measured Winners

| Workload | InterfaceDB / SegmentCore | SQLite result | Current lead |
|---|---:|---:|---:|
| File provider, range query p50 | 0.193 ms | 0.234 ms | 1.21x faster |
| File provider, broad prefix query p50 | 2.030 ms | 2.141 ms | 1.05x faster |
| SegmentCore explicit batch 100 raw writes | 27,553 records/s | 26,191 records/s | 1.05x faster |
| SegmentCore explicit batch 1,000 raw writes | 166,914 records/s | 137,569 records/s | 1.21x faster |
| SegmentCore write-through batch 1 | 2,607 records/s | 301 records/s | 8.67x faster |
| SegmentCore write-through batch 1,000 | 303,783 records/s | 136,790 records/s | 2.22x faster |
| SegmentCore write-through independent autocommit | 4,368 records/s | 300 records/s | 14.54x faster |
| SegmentCore fresh-process point reads | 64,624 reads/s | 31,182 reads/s | 2.07x faster |
| Quick post-reclamation footprint | 200,672 B | 225,280 B | 1.12x smaller |

The tables intentionally separate provider families and durability strategies. Windows write-through is a qualified machine profile, not the portable explicit-flush default, and the file-provider quick gate is not a universal database ranking.

## Reading The Results Correctly

- Exact, suffix, and contains lookups have small result sets, so InterfaceDB's expression planning and candidate materialization overhead is visible.
- Range, broad conjunction, and broad prefix results amortize that fixed cost and can be competitive with the measured SQLite object materialization paths.
- SegmentCore can reopen its raw primary locator from a compact paged checkpoint map. Its persisted secondary-index catalog avoids payload hydration when valid, but the repository adapter still seeds physical-key metadata and SQLite retains the smaller fixed open cost.
- Strong-object results describe a hot object cache. Metadata-only mode includes file reads and deserialization and must be measured separately for cold/local-storage workloads.
- “End to end” means from each public storage/query API through complete C# object materialization and validation. It does not claim a physical-media read when an OS page cache or InterfaceDB object cache satisfies the request.
- A 1,000-record quick gate is a regression and correctness signal. Public release claims should use the documented 10k/100k soak matrix on multiple machines and platforms.

## Reproduce

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick `
  -Output TestResults\production-gate-quick.json
```

Generated JSON, Markdown, traces, and BenchmarkDotNet output are ignored by Git. Promote only reviewed results into this document and record the environment, modes, workload, and object-materialization contract.

The original custom-engine admission result is documented in [Object-Native Storage Phase 0 Results](object-log-phase-zero-results.md). Its isolated explicit-flush prototype did not meet the 15% p50 advantage required by that gate.

The approved NVMe follow-up changed the broader architecture result. Hash-striped Windows write-through logs reached roughly 34,000 durable object saves/s for 512-byte objects, while one explicitly flushed group-commit log reached roughly 3,600 saves/s at concurrency 32 in virtualized Linux. The complete method, interleaved SQLite/file-provider comparisons, crash evidence, limitations, and conditional go decision are in [Object-Native Storage NVMe Prototype Results](object-log-nvme-prototype-results.md). These are prototype storage-layer results, not current `IRepository<T>` production-provider numbers.

The checkpointed primary-index/direct-read prototype became SegmentCore. Its historical admission evidence remains in [SegmentCore Prototype Results](segment-core-prototype-results.md); the maintained provider result above supersedes its implementation-status claims.

Those favorable exact-key/object-at-a-time paths were not the complete boundary. The earlier adversarial suite exposed scan, query, reopen, growth, and checkpoint stalls. Segment-order read-ahead, persisted InterfaceDB secondary indexes, background checkpoint publication, no-op/delta checkpoints, retained-WAL seeking, automatic paged locators, bounded WAL recycling, and bounded compaction now close the main implementation gaps; SQLite's smaller fixed open cost remains. See [SegmentCore Adversarial Strength And Weakness Results](segment-core-adversarial-results.md).

The subsequent mutation-bearing transaction WAL reduced explicit-transaction durability to one WAL flush while preserving direct object locators. In the latest five-iteration run, contiguous batch encoding/writes reached 166,914 records/s under explicit flush and 303,783/s under native Windows write-through at batch 1,000, versus SQLite FULL/WAL at 137,569/s and 136,790/s respectively. Concurrent group commit, bounded checkpoint-aware recycling, retained-prefix seeking, independent full/delta recovery roots, and forced-process termination on both sides of WAL replacement are implemented. See [SegmentCore Transactions And Performance](segment-core-transactions.md).

Use the production-candidate table above for the current code state. The clearest remaining SQLite advantages are fixed open cost, mature replication/tooling, multiple-process coordination, and relational capabilities. SegmentCore now includes verified point-consistent backup/restore, but not SQLite's broader ecosystem.
