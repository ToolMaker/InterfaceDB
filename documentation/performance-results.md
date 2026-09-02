# Current Performance Results

## Scope

These numbers are reproducible development snapshots, not universal product rankings. The first gate covers the file provider, and the SegmentCore transaction/query sections use complete-object comparison contracts. Raw storage-core reads, locator reopen, and metadata-open measurements are identified separately; they are not substitutes for repository-to-object query results.

- File-provider gate snapshot: 2026-09-02; later sections state their own evidence dates where relevant
- SDK: .NET 10.0.301
- Runtime used by the gate: .NET 10.0.9
- OS: Microsoft Windows 10 Pro 10.0.19045
- CPU: AMD Ryzen 9 3950X, 16 cores / 32 logical processors
- Storage: Samsung SSD 980 PRO 1 TB NVMe
- Records: 1,000
- Cache: InterfaceDB `StrongObject` hot object cache; SQLite page cache with a newly mapped object per returned row
- Seed contract: one awaited object write per operation
- Durable comparison: InterfaceDB `Durable`; SQLite `synchronous=FULL`
- Query contract: return complete objects, touch every field, then validate count and checksum
- Startup contract: restart at a provider-specific boundary—InterfaceDB deserializes the stored objects while rebuilding its provider and indexes; SQLite clears its pool and opens a connection without querying rows; the operating-system cache remains warm, and the metric ends before a separately issued result query
- InterfaceDB gate policy: single serialized write buffer, notifications/watcher disabled, hydration disabled, and concurrency bounded at 16

## Durable Results

The InterfaceDB query rows in this curated snapshot reuse `StrongObject` instances populated during the startup index rebuild. SQLite direct and Dapper still instantiate a complete `GateRecord` for every returned row. This is a valid hot-cache application comparison, but it is not canonical evidence for fresh storage-to-object materialization. That comparison requires the `MetadataOnly` gate (and, for a physical cold-media claim, a separately controlled cold-cache run).

| Operation | InterfaceDB | SQLite direct | SQLite + Dapper |
|---|---:|---:|---:|
| Independent object write, p50 | 5.574 ms | 1.927 ms | 1.936 ms |
| Independent object write, p95 | 6.035 ms | 2.035 ms | 2.036 ms |
| Exact query, 1 object, p50 | 0.336 ms | 0.027 ms | 0.028 ms |
| Range query, 90 objects, p50 | 0.244 ms | 0.299 ms | 0.201 ms |
| Prefix query, 1,000 objects, p50 | 2.108 ms | 2.760 ms | 1.652 ms |
| Suffix query, 2 objects, p50 | 0.901 ms | 0.130 ms | 0.129 ms |
| Contains query, 4 objects, p50 | 0.269 ms | 0.121 ms | 0.120 ms |
| Broad indexed conjunction, 324 objects, p50 | 0.700 ms | 0.940 ms | 0.608 ms |
| Provider-specific startup, OS cache warm, before separate query | 174.742 ms (object deserialization plus provider/index rebuild) | 6.333 ms (connection open only) | 5.563 ms (connection open only) |

### Canonical Fresh-Object Query Audit

The `MetadataOnly` audit on 2026-09-02 used the same 1,000 records, five timed query iterations,
and the current all-six-field FNV materialization contract. InterfaceDB creates a new managed object
for every returned record. Each relational path maps every selected column into a `GateRecord`
before the timer stops; the SQL Server production-gate path also obtains an open pooled connection
for each query call. These are client-host measurements against the configured SQL Server; the report
does not capture its version or deployment topology, so its column is not a portable server ranking.
The p50 values are the canonical storage-to-fresh-object comparison for this run:

| Query | InterfaceDB `MetadataOnly` | SQLite direct | SQLite + Dapper | SQL Server |
|---|---:|---:|---:|---:|
| Exact, 1 object | 0.484 ms | 0.027 ms | 0.029 ms | 0.191 ms |
| Range, 90 objects | 8.764 ms | 0.201 ms | 0.255 ms | 0.466 ms |
| Prefix, 1,000 objects | 95.357 ms | 1.695 ms | 2.271 ms | 3.086 ms |
| Suffix, 2 objects | 86.351 ms | 0.126 ms | 0.131 ms | 1.758 ms |
| Contains, 4 objects | 0.617 ms | 0.115 ms | 0.125 ms | 2.654 ms |
| Broad conjunction, 324 objects | 26.576 ms | 0.624 ms | 0.800 ms | 1.178 ms |

Two-second mixed read/write stress at concurrency 2 completed with zero errors and matching final data:

| Engine | Operations/s | p50 | p95 | p99 | Integrity |
|---|---:|---:|---:|---:|---|
| InterfaceDB Durable | 940.9 | 1.023 ms | 6.156 ms | 8.384 ms | Pass |
| SQLite direct FULL | 1,074.1 | 0.474 ms | 1.995 ms | 3.583 ms | Pass |
| SQLite + Dapper FULL | 951.0 | 0.628 ms | 2.013 ms | 3.764 ms | Pass |

The forced-termination cycle recovered 18 committed records, left zero proposal artifacts, and passed final validation.

## Historical Performance-Mode Results (2026-08-26)

The 2026-08-26 gate measured InterfaceDB Performance mode at 0.499 ms median and 1,697 independent writes/s. SQLite NORMAL measured 0.099 ms direct and 0.087 ms through Dapper. Two-second mixed stress reached 1,745 operations/s for InterfaceDB, 2,057/s for SQLite direct, and 2,183/s through Dapper; all integrity checks passed. Performance mode writes the canonical object file directly and does not claim the same crash-recovery behavior as Durable mode.

## File-Provider Optimization Result

The earlier Durable implementation measured 15.299 ms p50 per independent object write. Profiling and a filesystem protocol probe identified redundant persistence work in ephemeral locks and an overlapping payload flush. The current release-gate rerun measured 5.574 ms p50:

| Durable file-provider revision | p50 | Relative throughput |
|---|---:|---:|
| Earlier protocol | 15.299 ms | 1.00x |
| Current protocol | 5.574 ms | 2.74x |

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

Mutation-WAL checkpoints measured 15.4-20.9 ms under explicit flush and 9.7-14.5 ms under write-through for the 5,000-object atomic workloads. SegmentCore provider-open completed in 5.0-13.0 ms versus SQLite connection-open at 0.36-0.55 ms; neither reopen number includes the separately timed complete-object scan. Complete-object materialization remained workload-dependent. Missing or corrupt retained-WAL seek sidecars still fall back to complete WAL validation.

With 5,000 live objects and two obsolete update generations, bounded compaction reclaimed 2,154.9 KiB, reducing 6,740.4 KiB to 4,585.5 KiB. Stripe-by-stripe preparation, 64-record copy slices, and checkpoint publication outside the writer barrier limited the longest observed exclusive pause to 14.88 ms under explicit flush and 8.79 ms under Windows write-through. Total maintenance was 178.25 ms and 148.84 ms respectively across 89 short slices.

### Configurable Hash Routing

The current preview adds versioned `Fnv1a32`, `Fnv1a64`, `XxHash64`, and `XxHash3` routing choices
without changing the original `Fnv1a32`/seed-zero format. A 500,000-key, 16-stripe run measured zero
allocation for every algorithm. Its five-iteration, 20,000-record raw-payload write/checkpoint/reopen control was:

| Algorithm | Isolated routing vs default | End-to-end writes vs default |
|---|---:|---:|
| `Fnv1a32` | 1.00x | 1.00x |
| `Fnv1a64` | 0.96x | 0.97x |
| `XxHash64` | 1.48x | 1.02x |
| `XxHash3` | 1.96x | 1.04x |

Removing a duplicate internal whitespace scan improved default geometric-mean write throughput by
about 3% across sequential, common-prefix, and GUID keys while preserving validation at public
entry points. A separate five-iteration/100,000-record raw-payload confirmation measured `XxHash3` at 1.86x
isolated routing and 1.05x writes. The gain remains workload-dependent, so `Fnv1a32` stays the
compatibility default and `XxHash3` is an opt-in candidate.

The post-change SQLite sanity run retained the default route and reached 2,634, 15,971, 103,762, and
306,031 SegmentCore records/s at batches 1, 10, 100, and 1,000 versus SQLite's 297, 3,020, 27,813,
and 143,627. Independent writes were 4,303/s versus 303/s. This three-iteration sanity result checks
for regression; the five-iteration table above remains the maintained release comparison.

The provider passes 93 dedicated tests on Windows and Linux. Forced process termination after durable commit and on both sides of WAL replacement recovered every committed object in both final transaction runs. Linux additionally exposed and now regression-tests a fresh-segment combined-batch rotation defect: recovery was intact, but a large first batch could exceed the configured segment bound. See [SegmentCore Storage Provider](segment-core-storage-provider.md) for operations, limitations, and raw report names.

### Repository-Level Production Pilot

The direct storage-core transaction comparison above isolates the physical engine. A separate maintained pilot goes
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

The current write-through storage-core gate uses 256-byte deterministic payloads, 1,000-record atomic batches,
16 producers, eight stripes, a full primary checkpoint, an immediate unchanged checkpoint,
streaming integrity verification of every live frame, checkpoint reopen with zero tail replay, and
10,000 fully validated raw-payload point reads. It does not instantiate repository objects.

| 1,000,000 records | Ingestion | Full checkpoint | No-op checkpoint | Streaming integrity | Raw-store reopen / allocation | Sampled raw-payload reads |
|---|---:|---:|---:|---:|---:|---:|
| Resident locator | 286,655 records/s | 653.2 ms | 1.5 ms / 1,728 B | 403,335 records/s | 927.2 ms / 139.9 MiB | 131,930 reads/s |
| Paged locator | 278,641 records/s | 915.2 ms | 1.4 ms / 1,728 B | 408,379 records/s | 42.6 ms / 25.2 MiB | 79,774 reads/s |

Both modes passed count, checksum, full-payload, checkpoint-generation, and zero-tail-replay
assertions. Paged reopen was 21.8x faster and allocated 82.0% less, at the cost of a 40.1% slower full
checkpoint and 39.5% lower sampled random-read throughput. Automatic mode selects paging from
100,000 live entries. The repository adapter still materializes envelope key skeletons even when
the underlying raw locator is paged.

### IL/JIT And Allocation Audit

The .NET 10 full-optimization JIT output for `Crc32C.Append` is already a 212-byte hardware-intrinsic loop using 64-bit, 32-bit, and byte-tail `crc32` instructions. `Span` and `BinaryPrimitives` operations are inlined and the hot loop contains no managed calls, so handwritten IL would add maintenance risk without a faster instruction shape. A fused two-checksum prototype was 3.6% slower and was reverted. A custom eight-pass radix sort also failed to beat warmed `Array.Sort` reliably and was not shipped.

Measured managed changes that did earn their place are:

- zero-allocation ASCII key validation plus cached segment paths/handles raised fresh-process raw-payload point reads from 55,209/s to 62,166/s and cut read-path allocation from 33.1 MB to 8.4 MB for 25,000 reads;
- direct primitive checkpoint arrays and streamed page-map validation cut one-million-entry paged reopen allocation from 72.4 MB to roughly 24-26 MB;
- validating the authoritative checkpoint and derived page map in parallel reduced repeated warm paged reopen from roughly 45.8 ms to 26.4 ms; the final scale gate measured 42.6 ms without controlled operating-system cache eviction;
- publishing a large page map in parallel with the authoritative checkpoint reduced the three-run checkpoint median from 1,009.5 ms to 962.6 ms (4.65%);
- increasing the fully validating WAL SHA-256 stream buffer from 64 KiB to 1 MiB reduced the matched 268 MB-prefix reopen median from 215.7 ms to 209.8 ms (2.7%), while preserving full-prefix verification;
- the scale validator itself previously allocated an interpolated failure message for every valid payload byte. Correcting that benchmark defect changed 64 sampled 1 MiB reads from 6.83 s/7.60 GB allocated to 85.9 ms/67.2 MB before subsequent reader improvements. This was a benchmark correction, not a claimed engine speedup.

An O(1) retained-WAL shortcut was deliberately not implemented because it would stop byte-for-byte verification of checkpoint-covered WAL history. That recovery-integrity trade-off requires an explicit product decision rather than a hidden performance patch.

### Release Qualification Snapshot

- A genuine larger-than-memory raw-store run stored and integrity-checked 34,000 independent 1 MiB payload records: 35.65 GB of live payload versus a 34.26 GB managed-memory budget (1.041x). It ingested at 143.9 records/s, verified at 989.1 records/s, reopened the provider in 932.8 ms with a fully verified 755 MB retained WAL prefix, and sampled complete 1 MiB payloads at 502.5 reads/s. It did not instantiate repository objects.
- A five-minute Windows raw-store run completed 81,473,282 mixed operations with concurrent writes, four readers, one-second checkpoints, exact payload-model comparison, full integrity verification, 72.99 ms provider reopen, and passing injected disk-exhaustion recovery.
- The current .NET 10 Linux container passes all 123 SegmentCore tests, including symlink-alias ownership and shutdown-admission races, with zero skips. The separately maintained full Phase 0 run passed all 49 integration scenarios and 12/12 forced-termination cycles but failed the performance admission threshold: explicit flush was only 5.4% faster than SQLite direct at p50, below the required 15%. The current three-cycle quick rerun also passed correctness but led by only 11.1%. This is a disclosed loser and remains virtualized evidence, not bare-metal certification.
- The current 30-second Windows raw-store mixed-load gate completed 12,641,604 operations, reopened the provider in 17.91 ms, and passed injected disk-exhaustion recovery.
- A locally retained package `1.0.0-preview.2` baseline to current-source upgrade validation passed open/read/mutate/checkpoint/integrity/backup/restore/reopen, and freshly packed packages passed an isolated clean-consumer round trip.
- The corrected 2026-09-02 fresh-process comparison stored the same set of 100,000 deterministic 256-byte `PhaseZeroCodec` payloads in both engines and performed 25,000 deterministic point reads per worker. Both SegmentCore and SQLite decoded every payload, instantiated a six-field `GateRecord`, validated every field and content character, and reported the same all-field checksum. Across the final three consecutive runs, median post-open throughput was 67,442 complete objects/s for SegmentCore versus 38,806 objects/s for SQLite (1.74x); SQLite statement creation and preparation happened before its read timer. Median read-path allocation was 28.0 MB versus 42.5 MB. SegmentCore raw-store activation was 83.07 ms p50; SQLite read-only connection open was 16.32 ms p50. These are different provider-specific startup responsibilities and are reported as context, not an equal-work winner/loser ranking. The operating-system cache was not evicted, and the SegmentCore read-process worker captured stage telemetry.
- The older 64,624-versus-31,182 result remains historical raw-payload evidence only: that earlier harness returned and validated `byte[]` from both engines without instantiating `GateRecord`.
- Four concurrent processes reading one SQLite store all succeeded. SegmentCore supports one owning process per live store today; only one of four Windows workers and zero of four simultaneously racing Linux workers opened the same store. This is a measured product boundary, not a hidden qualification pass.
- Physical process-kill recovery passes, but actual removal of system power and a post-reboot cold-media measurement still require dedicated hardware.

### SegmentCore Startup And Repository Lifetime

The 2026-09-02 startup pass separates physical store activation from the repository/index lifecycle.
It also fixes repository initialization as a single-flight, application-lifetime operation rather
than work that can be repeated by concurrent callers.

| 100,000-record fresh-process metric | Result | Materialization evidence |
|---|---:|---|
| Raw SegmentCore activation, final read-process rerun | 83.07 ms p50, 3 runs | No object read; 82.81-85.81 ms |
| Cold repository initialization, catalog deliberately absent | 5,405.07 ms p50, 5 runs | Exactly 100,000 payload objects deserialized; 1,206,765,504 B allocated p50 |
| Valid-warm-catalog repository initialization | 2,172.41 ms p50, 5 runs | Zero payload objects deserialized; 460,131,976 B allocated p50 |
| Cold time through first fully materialized six-field object | 5,414.12 ms p50, 5 runs | 100,001 objects observed after the read |
| Valid-warm time through first fully materialized six-field object | 2,243.65 ms p50, 5 runs | Exactly one object observed after the read |
| Repeated `IRepository.InitializeAsync` in the same provider lifetime | 0.0087 ms p50, 10 calls | Zero deserializations and zero allocated bytes |

The raw open change overlaps independent checkpoint/WAL/object-log recovery work while retaining
full validation. Against the same harness's 103.5161 ms pre-change observation, the final 83.0657 ms
median is 19.76% lower; the baseline is a single observation, so this is a local regression result,
not a general hardware claim. The full-lifetime `FileShare.None` root lease remains in place before
recovery begins.

Warm catalog installation was separately profiled. Caching runtime-type resolution per distinct
type and deferring contains-index compaction until bulk installation completes reduced a matched
one-run warm initialization from 3,888.31 ms/1,162,553,440 B to 2,341.67 ms/463,083,968 B: 39.78%
lower latency and 60.17% fewer allocations. The final five-run median above was 2,172.41 ms. The
catalog format did not change, valid warm initialization still deserializes no payloads, and the
index is queryable during load through its mutation overlay.

These repository startup values are not SQLite comparisons. They include service registration,
provider construction/resolution, repository initialization, persisted index installation or
rebuild, and explicit object-count validation. The startup runner uses a new process and a
byte-identical store clone for every sample, but it does not evict the operating-system page cache.

### Current Measured Losers

| Workload | InterfaceDB provider | Best SQLite result | Current gap |
|---|---:|---:|---:|
| File provider, durable independent write p50 | 5.574 ms | 1.927 ms | 2.89x slower |
| File provider, `StrongObject` hot-cache exact query p50 | 0.336 ms | 0.027 ms | 12.25x slower |
| File provider, `StrongObject` hot-cache range query p50 | 0.244 ms | 0.201 ms | 1.21x slower |
| File provider, `StrongObject` hot-cache prefix query p50 | 2.108 ms | 1.652 ms | 1.28x slower |
| File provider, `StrongObject` hot-cache suffix query p50 | 0.901 ms | 0.129 ms | 6.96x slower |
| File provider, `StrongObject` hot-cache contains query p50 | 0.269 ms | 0.120 ms | 2.24x slower |
| File provider, `StrongObject` hot-cache broad query p50 | 0.700 ms | 0.608 ms | 1.15x slower |
| File provider, performance-mode write p50 | 0.499 ms | 0.087 ms | 5.74x slower |
| SegmentCore explicit batch 1,000 plus checkpoint | 98,420 records/s | 116,250 records/s | 1.18x slower |
| SegmentCore post-checkpoint footprint, batch 1,000 | 2,384,636 B | 733,184 B | 3.25x larger |
| Quick adversarial space reclamation | 104.78 ms | 21.78 ms SQLite VACUUM | 4.81x slower |
| Concurrent processes on one live store | 1/4 opened | 4/4 opened | Unsupported boundary |
| Virtualized-Linux strict p50 admission | 5.4% lead | 15% required | Gate failed |

### Provider-Specific Startup Context (Not Rankings)

| Startup boundary | InterfaceDB result | Relational context | Why it is not ranked |
|---|---:|---:|---|
| File provider startup | 174.742 ms | SQLite 6.333 ms; Dapper 5.563 ms | InterfaceDB deserializes objects and rebuilds indexes; the relational paths only open a connection |
| SegmentCore raw physical activation, 100,000 records | 83.07 ms p50 | SQLite read-only connection open 16.32 ms p50 | SegmentCore validates manifests, checkpoint, WAL, and object-log state; neither side creates the first object |
| SegmentCore valid-warm full repository initialization | 2,172.41 ms p50 | No equivalent measured | Includes DI construction and full in-memory secondary-index installation, with zero payload deserialization |
| SegmentCore cold full repository initialization | 5,405.07 ms p50 | No equivalent measured | Includes 100,000 payload deserializations and secondary-index rebuild |

### Current Measured Winners

| Workload | InterfaceDB provider | SQLite result | Current lead |
|---|---:|---:|---:|
| SegmentCore explicit batch 100 encoded-object storage-core writes | 27,553 records/s | 26,191 records/s | 1.05x faster |
| SegmentCore explicit batch 1,000 encoded-object storage-core writes | 166,914 records/s | 137,569 records/s | 1.21x faster |
| SegmentCore write-through batch 1 | 2,607 records/s | 301 records/s | 8.67x faster |
| SegmentCore write-through batch 1,000 | 303,783 records/s | 136,790 records/s | 2.22x faster |
| SegmentCore write-through independent autocommit | 4,368 records/s | 300 records/s | 14.54x faster |
| SegmentCore + shared codec fresh-process post-open complete-object point reads | 67,442 objects/s | 38,806 objects/s | 1.74x faster |
| Quick post-reclamation footprint | 200,672 B | 225,280 B | 1.12x smaller |

The fresh-process object row is the replacement for the historical 64,624-versus-31,182 raw-payload result. It is a storage-core-plus-shared-codec comparison, not an `IRepository<T>` secondary-query result. The report records `GateRecord/PhaseZeroCodec-v1/all-fields`, requires `MaterializedObjects == Reads`, checks the deterministic digest in the parent process, and the current row is the median of three final runs.

Windows write-through is a qualified machine profile, not a universal database ranking.

## Reading The Results Correctly

- Exact, suffix, and contains lookups have small result sets, so InterfaceDB's expression planning overhead is visible even in the curated `StrongObject` hot-cache profile.
- Range, broad conjunction, and broad prefix results amortize that fixed cost. Their current rows show hot InterfaceDB object-cache behavior against SQLite's fresh row-to-object mapping; use `MetadataOnly` results for canonical fresh-materialization conclusions.
- SegmentCore can reopen its raw primary locator from a compact paged checkpoint map. Its persisted secondary-index catalog avoids payload hydration when valid, but the repository adapter still seeds physical-key metadata and SQLite retains the smaller fixed open cost.
- `StrongObject` results describe a hot managed object cache. `MetadataOnly` is the canonical fresh-object mode because it includes file reads and deserialization on every query; it is not automatically a cold-media run because the operating-system cache may remain warm.
- “End to end” applies only to explicitly labelled public object-query paths and means execution through complete C# object materialization and validation. Raw-payload and metadata-open metrics have narrower boundaries. The file-provider startup metric does deserialize objects for index reconstruction, but is not an equal-work query comparison because the relational paths only open connections.
- A 1,000-record quick gate is a regression and correctness signal. Public release claims should use the documented 10k/100k soak matrix on multiple machines and platforms.

## Reproduce

Reproduce the curated `StrongObject` hot-cache snapshot:

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick -Cache strong `
  -Output TestResults\production-gate-quick.json
```

Run the canonical fresh managed object-materialization profile separately:

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick -Cache metadata `
  -Output TestResults\production-gate-quick-metadata.json
```

Reproduce the SegmentCore raw-open/object-read and repository-lifecycle measurements separately:

```powershell
dotnet run -c Release --project StorageBenchmarks.Transactions -- `
  --segment-core-read-process --records=100000 --payload-bytes=256 `
  --batch-size=1000 --processes=4 --reads-per-process=25000 `
  --output=segment-core-read-process.json

dotnet run -c Release --project StorageBenchmarks.Transactions -- `
  --segment-core-startup --records=100000 --iterations=5 `
  --worker-timeout-seconds=300 --output=segment-core-startup.json
```

The startup runner disables synchronous open-stage listeners by default so instrumentation does not
perturb its headline measurements. Add `--capture-open-stages` only for a diagnostic run.

Generated JSON, Markdown, traces, and BenchmarkDotNet output are ignored by Git. Promote only reviewed results into this document and record the environment, modes, workload, and object-materialization contract.

The original custom-engine admission result is documented in [Object-Native Storage Phase 0 Results](object-log-phase-zero-results.md). Its isolated explicit-flush prototype did not meet the 15% p50 advantage required by that gate.

The approved NVMe follow-up changed the broader architecture result. Hash-striped Windows write-through logs reached roughly 34,000 durable object saves/s for 512-byte objects, while one explicitly flushed group-commit log reached roughly 3,600 saves/s at concurrency 32 in virtualized Linux. The complete method, interleaved SQLite/file-provider comparisons, crash evidence, limitations, and conditional go decision are in [Object-Native Storage NVMe Prototype Results](object-log-nvme-prototype-results.md). These are prototype storage-layer results, not current `IRepository<T>` production-provider numbers.

The checkpointed primary-index/direct-read prototype became SegmentCore. Its historical admission evidence remains in [SegmentCore Prototype Results](segment-core-prototype-results.md); the maintained provider result above supersedes its implementation-status claims.

Those favorable exact-key/object-at-a-time paths were not the complete boundary. The earlier adversarial suite exposed scan, query, reopen, growth, and checkpoint stalls. Segment-order read-ahead, persisted InterfaceDB secondary indexes, background checkpoint publication, no-op/delta checkpoints, retained-WAL seeking, automatic paged locators, bounded WAL recycling, and bounded compaction now close the main implementation gaps; SQLite's smaller fixed open cost remains. See [SegmentCore Adversarial Strength And Weakness Results](segment-core-adversarial-results.md).

The subsequent mutation-bearing transaction WAL reduced explicit-transaction durability to one WAL flush while preserving direct object locators. In the latest five-iteration run, contiguous batch encoding/writes reached 166,914 records/s under explicit flush and 303,783/s under native Windows write-through at batch 1,000, versus SQLite FULL/WAL at 137,569/s and 136,790/s respectively. Concurrent group commit, bounded checkpoint-aware recycling, retained-prefix seeking, independent full/delta recovery roots, and forced-process termination on both sides of WAL replacement are implemented. See [SegmentCore Transactions And Performance](segment-core-transactions.md).

Use the production-candidate table above for the current code state. The clearest remaining SQLite advantages are fixed open cost, mature replication/tooling, multiple-process coordination, and relational capabilities. SegmentCore now includes verified point-consistent backup/restore, but not SQLite's broader ecosystem.
