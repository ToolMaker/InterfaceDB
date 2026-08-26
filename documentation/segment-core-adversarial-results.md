# SegmentCore Adversarial Strength And Weakness Results

## Decision

This page is a historical adversarial snapshot of the pre-production prototype. At that point SegmentCore was a strong specialized engine for concurrent, independently durable object writes and warm exact-key reads, but was **not yet a general SQLite replacement** or ready to become InterfaceDB's default provider. Bounded compaction, persisted secondary indexes, non-blocking checkpoint publication, segment-order streaming, transactions, and integrity tooling have since been implemented. Use [SegmentCore Storage Provider](segment-core-storage-provider.md) for current status and results.

The original Phase Two matrix emphasized the path SegmentCore was designed to win. This follow-up deliberately measures paths where its current architecture should lose or expose an unsupported capability. An execution `PASS` means every measured result was correct and the benchmark completed; it is not a production-admission result.

Measurement date: 2026-08-25.

## Fairness And Scope

The main adversarial run used:

- .NET 10.0.9 on Windows 10.0.19045;
- Samsung SSD 980 PRO 1 TB NVMe, NTFS;
- 2,000 live objects with approximately 512-byte content;
- concurrency 16;
- SegmentCore with eight write-through stripes and an immutable primary-index checkpoint;
- SQLite WAL with `synchronous=FULL`, one autocommit per object mutation, and its persistent secondary indexes;
- complete `GateRecord` serialization/materialization and checksum verification;
- three complete-scan/query iterations;
- five complete update generations, followed by deleting half the live set;
- SegmentCore checkpoint publication and SQLite WAL truncation after every update generation;
- SQLite `VACUUM` after deletion; the prototype measured here did not yet have SegmentCore compaction;
- a separate 50,000-record SegmentCore store for foreground writes during checkpoint publication.

The operating-system page cache remains available to both engines. These are warm local-storage measurements, not physical cold-media claims.

## Where SegmentCore Is Strong

| Workload | SegmentCore | SQLite FULL | Result |
|---|---:|---:|---:|
| Independent durable object writes | 27,167 ops/s | 295 ops/s | SegmentCore 92.1x faster |
| Warm exact read plus object materialization | 123,556 ops/s | 46,643 ops/s | SegmentCore 2.65x faster |
| Repeated update generation | approximately 30k-32k ops/s | approximately 300-320 ops/s | SegmentCore roughly 100x faster |
| Independent durable deletes | approximately 29k ops/s | approximately 311 ops/s | SegmentCore roughly 93x faster |

This is the engine's genuine advantage: reuse append handles, spread independent durable frames across NVMe stripes, and resolve exact keys through an in-memory locator.

## Where SegmentCore Is Weak

### Scans And Unintegrated Secondary Queries

SegmentCore currently has only its primary locator inside the prototype. The benchmark therefore uses the only complete fallback available today: enumerate every key, issue a direct frame read, validate the frame, deserialize the object, and evaluate the predicate. SQLite can scan pages in storage order or use a persistent index.

| Workload | SegmentCore p50 | SQLite p50 | Throughput disadvantage |
|---|---:|---:|---:|
| Complete materialized scan | 51.881 ms | 3.396 ms | SegmentCore 12.0x slower |
| Role plus age conjunction/range | 42.039 ms | 1.140 ms | SegmentCore 34.3x slower |
| Prefix query | 40.455 ms | 1.718 ms | SegmentCore 18.8x slower |
| Suffix query | 37.831 ms | 0.243 ms | SegmentCore 114.8x slower |
| Literal contains query | 40.947 ms | 1.035 ms | SegmentCore 34.9x slower |

This did not prove that RadixForge or InterfaceDB's range/contains indexes were slow. It proved that the prototype measured here had not yet persisted and integrated them. The production-candidate provider now persists checksummed secondary-index catalogs; see [SegmentCore Storage Provider](segment-core-storage-provider.md). This page remains the historical adversarial baseline.

The complete scan also exposes a separate physical-layout problem: reading current objects in hash-key order causes one direct frame read per object instead of a segment-order sequential cursor.

### Reopen And Recovery

| Operation | SegmentCore | SQLite FULL |
|---|---:|---:|
| Checkpoint reopen | 15.502 ms; 431,848 B allocated | 4.258 ms; 2,640 B allocated |
| SegmentCore full recovery with checkpoint removed | 35.747 ms; 7,541,096 B allocated | No equivalent catalog-loss step required |

At this small cardinality, SegmentCore checkpoint reopen was 3.6x slower and allocated about 164x more managed memory than SQLite reopen. Removing the checkpoint forced SegmentCore to scan 2,000 frames and more than doubled reopen time.

The 64 KiB payload confirmation made the recovery cost clearer: recovering only 500 live objects without a checkpoint took 76.880 ms and allocated approximately 132 MB. The current full-recovery implementation materializes payloads while rebuilding locators and performs overlapping recovery work when opening stripe writers. That is a concrete optimization target.

### Unbounded Append History Without Compaction

| Stage | SegmentCore | SQLite maintained | SegmentCore/live payload | SQLite/live payload |
|---|---:|---:|---:|---:|
| Initial 2,000 objects | 1.485 MB | 1.393 MB | 1.25x | 1.18x |
| Five full update generations | 8.473 MB | 1.393 MB | 7.16x | 1.18x |
| Half deleted | 8.515 MB | 0.721 MB | 14.37x | 1.22x |

SegmentCore's initial density is competitive. Its storage then grows almost linearly with every update generation because old versions and tombstones cannot be reclaimed. After the measured churn and deletion, SegmentCore used 11.8x the physical storage of maintained SQLite.

With 64 KiB values, three update generations plus deleting half left SegmentCore at 131.49 MB versus 16.59 MB for maintained SQLite. The absolute penalty scales with payload size, so compaction is an operational requirement rather than an optional optimization.

### Checkpoint Foreground Stall

The current primary checkpoint uses a global mutation barrier. On the 50,000-record checkpoint-stall store:

- baseline foreground p99 update latency was 1.391 ms;
- p99 while checkpointing was 21.609 ms;
- foreground throughput fell from 33,337 to 14,192 operations/s;
- the measured checkpoint took 20.663 ms.

The p99 penalty was 15.5x. Reads remain available, but all new mutations wait for the checkpoint barrier. SQLite's repeated WAL checkpoint pressure reduced measured foreground update throughput from 313 to 285 operations/s in the corresponding maintenance probe; its exact tail latency result was noisy, but it did not require rebuilding an in-memory object locator under one global application-level mutation barrier.

### Large-Value Boundary

The 500-object, 64 KiB confirmation showed:

- SegmentCore durable writes remained 41.1x faster;
- warm exact reads were effectively tied and SegmentCore was 9% slower in this run;
- SegmentCore's complete scan was 1.23x slower;
- indexed/constrained queries still favored SQLite, except literal contains where both engines scanned the large text and were effectively tied;
- checkpoint reopen still favored SQLite by 3.9x.

SegmentCore's exact-read advantage therefore narrows as payload copy, CRC work, UTF-8/MessagePack decoding, and allocation dominate locator lookup.

## Unsupported Or Unqualified Capabilities At The Measurement Date

| Capability | SegmentCore today | SQLite comparison |
|---|---|---|
| Independent concurrent writer/store instances | Rejected with `IOException` | Independent connections supported |
| Atomic multi-object transaction | Implemented and qualified separately; slower than SQLite in the current protocol | Forced two-object rollback verified |
| Online obsolete-version/tombstone reclamation | Not implemented | WAL checkpoint and `VACUUM` available |
| Persistent secondary range/prefix/suffix/contains indexes | Not integrated | Persistent B-tree indexes available; contains can still scan |
| Recovery without primary checkpoint | Correct but O(total log history) | Primary B-tree remains part of the database |
| Cold cache and data larger than RAM | Not qualified | Still workload-dependent, but mature behavior exists |
| Sudden power-loss qualification | Not completed | SQLite has a mature durability history; hardware/filesystem still matter |

Single-writer ownership remains a product boundary. Store-local multi-object transactions are now implemented, while nested, distributed, multi-process, and cross-provider transactions remain unsupported. See [SegmentCore Transactions And Performance](segment-core-transactions.md).

## Historical Recommended Engineering Order

1. **Incremental compaction and capacity control.** This is the largest operational failure and worsens with every update and large payload.
2. **Persist and integrate InterfaceDB secondary indexes.** Exact lookup wins do not compensate for 19x-115x query losses caused by full-scan fallback.
3. **Replace the global checkpoint mutation barrier.** Use an epoch/copy-on-write locator snapshot or checkpoint deltas so foreground mutations continue during checkpoint serialization.
4. **Add a segment-order scan cursor.** Complete scans should issue sequential reads and materialize each current version once.
5. **Eliminate duplicate full-recovery payload materialization.** Rebuild locators without retaining live payload arrays and avoid a second stripe recovery pass.
6. **Reduce reopen memory and introduce lazy/on-disk locator options** for stores whose primary index should not be fully resident.
7. **Productionize transaction scoping** with coordinator-journal recycling, crash-window/power-loss qualification, repository-layer integration, and explicit capability boundaries.
8. **Run cold-cache, larger-than-RAM, bare-metal Linux, and physical power-loss gates.**

The future RadixForge-directed bucket experiment remains useful for write distribution and prefix locality, but it should follow compaction and secondary-index integration. It does not solve obsolete history, global checkpoint stalls, or atomicity.

## Reproduce

Main 512-byte adversarial run:

```powershell
dotnet run --project StorageBenchmarks.Transactions\StorageBenchmarks.Transactions.csproj `
  -c Release -- --segment-core-limits `
  --output artifacts\segment-core-limits\windows-nvme-2000-complete.json
```

Large-value confirmation:

```powershell
dotnet run --project StorageBenchmarks.Transactions\StorageBenchmarks.Transactions.csproj `
  -c Release -- --segment-core-limits --quick --payload-bytes 65536 `
  --output artifacts\segment-core-limits\windows-nvme-500-64k-complete.json
```

The runner writes both JSON and Markdown reports. It validates counts and materialized checksums, verifies SQLite transaction rollback, confirms SegmentCore's single-writer behavior, and fails its execution result on any integrity error.
