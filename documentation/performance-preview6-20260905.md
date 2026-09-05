# Preview.6 performance snapshot — 5 September 2026

These are development measurements, not universal rankings. Lower elapsed time is better. SQL means SQLite, not SQL Server. This public report covers the MIT FileSystem and SegmentCore providers.

## Complete-object SegmentCore versus SQLite

Previous = 2 September 2026; current = 5 September 2026. Five samples per workload. Windows 10 Pro 10.0.19045 x64; .NET SDK 10.0.301, runtime 10.0.9; AMD Ryzen 9 3950X (16 cores/32 threads); Samsung 980 PRO 1 TB NVMe. SegmentCore uses eight stripes, mutation WAL and WriteThrough; SQLite uses Microsoft.Data.Sqlite 10.0.9, WAL, synchronous=FULL, disabled automatic checkpointing and disabled pooling.

Times are milliseconds for the **entire workload**, not each object. Write timings include encoding/binding and durable acknowledgement. Reads instantiate six-field GateRecord objects and consume every field, with count/checksum validation. This is the physical core plus PhaseZeroCodec, not full IRepository or secondary-index querying.

| Workload | SQLite previous → current ms | SegmentCore previous → current ms | Current SegmentCore result |
| --- | ---: | ---: | --- |
| Write 5,000; atomic batch 1 | 16,080.79 → 16,395.25 | 1,522.73 → 1,380.23 | Win 11.88× |
| Write 5,000; atomic batch 10 | 1,652.83 → 1,646.37 | 245.43 → 238.79 | Win 6.89× |
| Write 5,000; atomic batch 100 | 178.21 → 180.21 | 38.32 → 34.65 | Win 5.20× |
| Write 5,000; atomic batch 1,000 | 31.22 → 30.69 | 12.38 → 11.52 | Win 2.66× |
| Write 1,000; independent saves | 3,118.12 → 3,099.40 | 178.71 → 177.50 | Win 17.46× |
| Write 5,000 + checkpoint; batch 1,000 | 37.10 → 36.66 | 20.89 → 19.98 | Win 1.83× |
| Checkpoint after batch-1,000 writes | 5.88 → 5.98 | 8.52 → 8.46 | Loss 1.41× |
| First read 5,000; after batch-1 writes | 6.41 → 6.29 | 11.42 → 13.17 | Loss 2.09× |
| First read 5,000; after batch-10 writes | 6.68 → 6.64 | 4.26 → 6.01 | Win 1.10× |
| First read 5,000; after batch-100 writes | 6.87 → 6.65 | 4.11 → 6.92 | Loss 1.04× |
| First read 5,000; after batch-1,000 writes | 6.77 → 6.55 | 4.01 → 7.29 | Loss 1.11× |
| First read 1,000; after independent saves | 2.04 → 1.98 | 1.19 → 1.23 | Win 1.62× |

Write-plus-checkpoint is the sum of separate medians, matching this runner's inclusive-throughput calculation, not a separately sampled median of total time. There is one checkpoint after all 5,000 writes, not after each transaction. SQLite checkpointing and object-index checkpointing perform different work.

First reads follow fresh raw-store reopen without deliberate OS-cache eviction. They include first-read/JIT effects and are not steady-state or cold-media results. SegmentCore's after-batch-1,000 read rose from 4.0142 to 7.2859 ms (+81.5%): an observed regression to investigate, not a proven attribution to one change. Neither the read losses nor checkpoint cost should be hidden behind write-only wins.

Raw reopen was 7.1418 → 4.6090 ms for SegmentCore and 0.2451 → 0.2630 ms for SQLite. **No like-for-like win is awarded**: recovering an embedded store is different from opening a SQLite connection without returning an object. Neither is full repository initialization, and reopen is not paid on every query.

## Full IRepository: fresh objects

A separate harness exercises the complete repository/index/envelope/storage path. 2,000 records, 512-byte payload, MetadataOnly, seven samples after two warm-ups. Range queries return 500 objects, first-result queries one, scans 2,000, and save batches 128. Previous = the first optimization pass on 5 September; current = after both subsequent follow-ups. These baselines are **not** the 2 September SQL baseline.

File storage uses durable per-object proposals and locks; SegmentCore uses mutation-WAL batches with explicit flush. Save inputs are constructed outside timing; encoding, saving and complete returned-object consumption are inside it. There is no SQLite control in this matrix.

| Full repository workload | File previous → current ms | SegmentCore previous → current ms |
| --- | ---: | ---: |
| Repository/index initialization | 207.6956 → 191.8021 | 25.4697 → 24.6937 |
| Indexed range, fresh expression | 17.8807 → 16.5422 | 4.5408 → 3.8929 |
| Indexed first exact result | 0.1540 → 0.1329 | 0.0236 → 0.0208 |
| Scan all | 72.6192 → 69.1361 | 16.8536 → 15.0034 |
| Scan concrete implementation | Not measured → 62.4706 | Not measured → 16.1393 |
| Save batch; unchanged indexes | 162.7552 → 158.5666 | 4.3755 → 4.4045 |
| Save batch; changed indexes | 150.4505 → 158.0193 | 4.2852 → 4.3282 |

SegmentCore's range is 14.3% lower elapsed time and full scan 11.0% lower against that first-pass snapshot. File range is 7.5% lower and initialization 7.7% lower. Not everything improved: the file changed-index save rose 5.0%, and SegmentCore save medians were approximately flat/slightly higher.

### Final follow-up in isolation

A second run used the identical harness with preserved pre-final-follow-up binaries. Current ran before baseline; this is one ordered A/B pair, not repeated randomized evidence. For a reused bounded-range expression with StrongObject caching:

| Provider | Before final pass → current ms | Allocated bytes before → current |
| --- | ---: | ---: |
| FileSystem | 0.4254 → 0.4554 | 63,272 → 62,928 |
| SegmentCore | 0.4229 → 0.4371 | 62,904 → 62,560 |

The syntax cache saved 344 bytes per repeated query in these rows, but **did not demonstrate an elapsed-time improvement**. Fresh bounded expressions added about 416 bytes per call in the SegmentCore control. StrongObject timings describe reused cached objects, not fresh materialization.

## Implementation scope and correctness

Preview.6 includes bounded ordered envelope batches; duplicate/missing-key handling; file bulk-lease reuse and filtered prefetch; changed-field-only index maintenance; reusable predicate shapes with live captures; weak expression-syntax reuse; safe serialization buffer ownership; lower-allocation SegmentCore WAL/frame/checkpoint paths; and guarded ARM CRC support. Caching was not globally disabled to obtain these improvements.

Existing durability/publication boundaries remain: required flushes, full checkpoint checksum validation, failure recovery and one-writer-process ownership are retained. Checkpoint capture restoration preserves newer concurrent updates on failure. Custom serializers remain protected from pooled-memory lifetime violations.

Public-only release-checkout validation on Windows: 276 tests passed, four platform-specific skips (133 shared + 138 SegmentCore + five analyzer tests); all 50 executable integration scenarios passed; complete-object materialization controls passed for InterfaceDB MetadataOnly, SQLite direct and Dapper. The extracted public repository harness passed all 56 smoke cases across two providers and three cache modes, including final reopen verification. Smoke is correctness-only, not a new timing run.

The timed transaction suite also passed forced process termination after commit and immediately before/after WAL replacement, recovering all 128 committed objects in each scenario. Timed processes ran sequentially, without concurrent builds/tests; this was an ordinary developer machine, not an isolated laboratory.

Five/seven samples do not establish production p95/p99 guarantees. No SQL Server run, cache eviction, physical power-cut, Android ARM64 or Apple runtime performance qualification was performed in this pass. Prior mobile qualification is not evidence of a newly measured mobile speedup.

## Reproduction and provenance

The curated values above are retained from local reports:
- `artifacts/performance-run-20260905/sqlite-comparison.json`
- `artifacts/segment-core-comparison/fresh-write-through-20260902.json`
- `artifacts/performance-run-20260905/repository-current.json`
- `artifacts/performance-run-20260905/repository-before-final-followup.json`
- `artifacts/performance-20260905/after-repository-final.json`

Raw reports are intentionally not committed. The measured workspace included additional unpublished comparison participants; their adapters are excluded from this public release. The public harnesses retain the File/SegmentCore/SQLite workloads but do not reproduce that additional participant execution order exactly. Rebuilding the release also changes binary fingerprints; these values describe the recorded measurement builds, not a claim that the published package bytes were separately timed.

Measured working-tree base: `5a88905e5782cc38f4213f329a8e9db6ec2347de` plus the subsequently committed optimizations. Assembly SHA-256:
- Idb.Libraries: `4F09AC09B11DDD52F02A919CC8A3E6AC548D5E03931AB6DAEC04AE2A3E2DD428`
- SegmentCore: `B6F5BC0AFA3D88F15B265121120251A5E139E3DDB28AB76F4A709B4D3218FBE9`

Use new output filenames to preserve evidence:

```powershell
dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-transactions --records=5000 --independent-records=1000 --iterations=5 --batch-sizes=1,10,100,1000 --stripes=8 --protocols=wal --durability=write-through --output=artifacts/preview6-sqlite-rerun.json
dotnet run -c Release --project StorageBenchmarks.Repository -- --run --records 2000 --iterations 7 --warmup 2 --batch 128 --payload-bytes 512 --output artifacts/preview6-repository-rerun.json
```

See the [repository harness](../StorageBenchmarks.Repository/README.md) for cache modes and measurement boundaries, and [release notes](preview6-release-notes.md) for format compatibility.
