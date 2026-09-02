# SegmentCore Prototype Results

## Decision

SegmentCore, the checkpointed primary-index and striped segment prototype, is a **go for an isolated production-provider implementation**. It is not yet a replacement for the current file provider.

This is historical admission evidence for the exact-key and object-at-a-time architecture slice. The separate [SegmentCore Adversarial Strength And Weakness Results](segment-core-adversarial-results.md) identified compaction, persistent secondary-index integration, non-blocking checkpoints, efficient scans/recovery, and explicit capability boundaries as necessary work. Those implementation-status statements are superseded by the current [SegmentCore Storage Provider](segment-core-storage-provider.md); the measurements remain design history.

The prototype retained the striped log's durable-write advantage while adding the minimum complete object-store path:

- durable save/upsert;
- exact primary-key lookup;
- complete object deserialization;
- update and tombstone delete;
- crash-safe primary-index checkpoints;
- checkpoint-plus-tail restart recovery;
- concurrent mixed reads and writes.

On the native Windows/NVMe development machine, concurrent 512-byte indexed saves retained more than 95% of the preceding append-only result. At 100,000 live objects, the initial prototype sustained roughly 36,000 durable saves/s, 401,000 fully materialized reads/s after reopen, and 165,000 operations/s in the measured mixed workload. The hardened SegmentCore rerun sustained roughly 34,000 durable saves/s and 147,000 mixed operations/s while reducing checkpoint allocation from 23.1 MB to about 102 KB.

Research and measurement date: 2026-08-25.

## SegmentCore Architecture Tested

The prototype adds a partitioned in-memory primary index over the existing checksummed striped logs:

```text
(type/key)
    -> primary hash lookup
    -> (stripe, segment, offset, frame length, LSN)
    -> direct RandomAccess read from a reused segment handle
    -> frame/header/footer CRC validation
    -> payload decode into the complete C# object
```

Important behavior:

- object payloads are not duplicated in the index;
- a locator is published only after the corresponding log mutation is durable;
- same-key updates and tombstones route to one stripe and use LSN comparison, preventing an older continuation from overwriting a newer locator;
- checkpoints contain only keys, locators, and one replay position per stripe;
- checkpoint publication uses a checksummed, write-through, immutable generation;
- reopen loads the newest valid checkpoint and replays only later frames;
- a corrupt newest checkpoint falls back to an older valid generation and replays the additional tail;
- reads reuse open segment handles and remain safe while later records append because published frames are immutable;
- checkpoint creation establishes a mutation barrier, waits for active mutations, streams the stable locator table and stripe positions to an immutable generation, publishes it, and then releases blocked mutations;
- each stripe has a bounded queue with explicit wait/backpressure or fail-fast rejection behavior and telemetry;
- checkpoint creation and loading use bounded working buffers, and old checkpoint generations are reclaimed according to configured retention.

This was the historical admission prototype. Its production successor now lives in `InterfaceDB.Storage.SegmentCore` and integrates through `IEnvelopeRepository<TContract>`; see [SegmentCore Storage Provider](segment-core-storage-provider.md).

The subsequent transaction slice adds store-local atomic multi-object commits without changing direct record locators. See [SegmentCore Transactions And Performance](segment-core-transactions.md) for protocol, API, recovery semantics, limitations, and the SQLite comparison.

## Fairness Contract

The SQLite comparisons use:

- WAL mode and `synchronous=FULL`;
- one autocommit per object save, update, or delete;
- a primary key and no secondary indexes;
- complete binding of all six object fields for writes;
- complete six-column `GateRecord` materialization for reads;
- both `Microsoft.Data.Sqlite` and a separate Dapper confirmation;
- the same object-at-a-time public acknowledgement boundary as the custom store.

The custom read includes primary lookup, segment I/O through `RandomAccess.ReadAsync`, frame and payload CRC validation, payload extraction, and complete `GateRecord` decoding. Warm and post-reopen results retain the operating-system page cache for every engine. They are end-to-end object-materialization measurements, not claims that every read reached physical media. Standalone `Reopen` rows stop after provider initialization and before the separately timed first materialized read.

The mixed workload is 80% exact reads, 10% updates, 5% inserts, and 5% deletes. Every run ends with a complete materialized snapshot and key/checksum validation.

## Native Windows/NVMe Results

Environment:

- Samsung SSD 980 PRO 1 TB NVMe;
- NTFS with 4 KiB physical sectors;
- Windows 10.0.19045;
- .NET 10.0.9.

The primary interleaved matrix used 500 measured objects per fresh store, three iterations, 5,000 exact reads, 2,000 mixed operations, payloads of 512 B, 4 KiB, and 64 KiB, concurrency 1/16/64, and 8/16 custom stripes.

Representative best custom results versus direct `Microsoft.Data.Sqlite`:

| Payload | Concurrency | Operation | Custom ops/s | SQLite ops/s | Ratio | Custom p50 | Custom p99 |
|---:|---:|---|---:|---:|---:|---:|---:|
| 512 B | 1 | Save | 6,129 | 271 | 22.6x | 0.146 ms | 0.387 ms |
| 512 B | 1 | Materialized read | 60,732 | 35,896 | 1.7x | 0.014 ms | 0.042 ms |
| 512 B | 16 | Save | 30,374 | 291 | 104.5x | 0.431 ms | 1.591 ms |
| 512 B | 16 | Materialized read | 514,930 | 48,267 | 10.7x | 0.024 ms | 0.082 ms |
| 512 B | 64 | Save | 32,676 | 259 | 126.0x | 1.732 ms | 3.956 ms |
| 512 B | 64 | Mixed 80/10/5/5 | 136,042 | 1,286 | 105.8x | 0.027 ms | 4.534 ms |
| 4 KiB | 16 | Save | 25,276 | 279 | 90.5x | 0.461 ms | 2.184 ms |
| 4 KiB | 16 | Materialized read | 256,899 | 25,081 | 10.2x | 0.032 ms | 0.955 ms |
| 4 KiB | 64 | Mixed 80/10/5/5 | 107,146 | 1,305 | 82.1x | 0.033 ms | 5.909 ms |
| 64 KiB | 16 | Save | 15,183 | 254 | 59.9x | 0.677 ms | 5.237 ms |
| 64 KiB | 16 | Materialized read | 26,444 | 9,464 | 2.8x | 0.329 ms | 3.260 ms |
| 64 KiB | 64 | Mixed 80/10/5/5 | 24,391 | 1,116 | 21.9x | 0.602 ms | 15.353 ms |

Report: `artifacts/phase-two/windows-nvme-full-20260825.json`.

## Hardened SegmentCore Confirmation

The hardened 10,000-object comparison at concurrency 16 measured:

| Operation | SegmentCore | SQLite + Dapper FULL | Ratio |
|---|---:|---:|---:|
| Durable save | 31,554 ops/s | 294 ops/s | 107.4x |
| Materialized read after reopen | 478,660 ops/s | 24,077 ops/s | 19.9x |
| Update | 32,453 ops/s | 295 ops/s | 110.2x |
| Delete | 31,116 ops/s | 302 ops/s | 103.0x |
| Mixed 80/10/5/5 | 144,060 ops/s | 1,441 ops/s | 100.0x |

The comparison preserves the same object-at-a-time durable acknowledgement boundary and complete object materialization contract. Report: `artifacts/phase-two/segmentcore-10k-dapper-20260825.json`.

At 100,000 objects, the hardened 16-stripe run measured 33,715 durable saves/s, 359,100 reads/s after reopen, and 146,809 mixed operations/s. The streaming checkpoint took 74.4 ms p50 and allocated about 102 KB, versus 93.4 ms and 23.1 MB in the earlier implementation. Reopen allocated 22.4 MB versus 32.9 MB; most remaining allocation is the required in-memory keys and locator dictionary. Report: `artifacts/phase-two/segmentcore-100k-streaming-20260825.json`.

The hardened run is a separate one-iteration observation, so its throughput delta from the earlier run should be treated as normal machine/run variance until a longer randomized matrix is repeated. Its strongest deterministic result is the allocation reduction.

## Earlier Dapper And Cardinality Confirmations

The 10,000-object Dapper confirmation at concurrency 16 measured:

| Operation | Custom | SQLite + Dapper FULL | Ratio |
|---|---:|---:|---:|
| Durable save | 35,089 ops/s | 305 ops/s | 114.9x |
| Materialized read after reopen | 508,866 ops/s | 26,715 ops/s | 19.0x |
| Update | 35,235 ops/s | 309 ops/s | 113.9x |
| Delete | 34,938 ops/s | 319 ops/s | 109.6x |
| Mixed 80/10/5/5 | 151,656 ops/s | 1,464 ops/s | 103.6x |

At that cardinality, an eight-stripe checkpoint took about 9.7 ms p50 and reopen took 15.5 ms. The custom store occupied about 14.1 MB after the complete mutation workload versus about 10.4 MB for SQLite's database/WAL/shared-memory files.

Report: `artifacts/phase-two/windows-nvme-10k-dapper-20260825.json`.

The 100,000-object custom scale run measured:

| Operation | Result | p50 | p99 |
|---|---:|---:|---:|
| Durable save | 36,226 ops/s | 1.403 ms | 6.627 ms |
| Materialized read after reopen | 400,911 ops/s | 0.087 ms | 2.083 ms |
| Update | 36,487 ops/s | 1.416 ms | 5.737 ms |
| Delete | 33,273 ops/s | 1.561 ms | 5.762 ms |
| Mixed 80/10/5/5 | 164,940 ops/s | 0.029 ms | 3.787 ms |
| Checkpoint | 93.4 ms p50 | - | 127.7 ms |
| Reopen | 70.5 ms | - | - |

The final 100,000-object mutation history occupied about 141 MB across 35 files. Checkpoint creation allocated about 23 MB and reopen allocated about 33 MB, making checkpoint streaming and allocation reduction explicit production work.

Report: `artifacts/phase-two/windows-nvme-100k-custom-scale-corrected-20260825.json`.

A separate five-iteration single-caller sweep showed that one stripe is best for small single-caller saves and retained roughly 7,400 saves/s for 512-byte objects. The recommended initial automatic policy is therefore one/four stripes for low concurrency and eight/sixteen for concurrent native-Windows NVMe workloads.

## Correctness And Recovery Evidence

- 33 dedicated managed prototype tests pass after hardening; 13 cover SegmentCore directly.
- Tests cover direct reads, updates, deletes, concurrent same-key publication ordering, concurrent unique saves/reads, checkpoint generation and retention, cancellation cleanup, checkpoint concurrent with saves, old-segment reads after rotation, no-checkpoint fallback, checkpoint-tail replay, fallback from a corrupt newest checkpoint, and bounded queue wait/reject overload behavior.
- Five out of five forced-process-termination cycles passed after a durable checkpoint. Report: `artifacts/phase-two/segmentcore-crash-five-20260825.json`.
- Every acknowledged post-checkpoint object was recovered and materialized correctly.
- Recovery may include valid frames whose callers died before observing acknowledgement; that outcome-unknown boundary is expected.

Forced process termination is not sudden power loss. Hardware power-cut qualification remains mandatory before a production durability claim.

## Admission Result

The previous admission target required the indexed path to retain at least 80% of the append-only write throughput while beating SQLite on fully materialized reads and mixed workloads.

The concurrent paths pass:

- 512 B/concurrency 64 retained about 95% of the preceding append-only throughput;
- 4 KiB/concurrency 64 retained about 89%;
- 64 KiB/concurrency 16 retained about 87%;
- single-caller 512 B retained about 93% when using the correct one-stripe policy;
- materialized reads beat SQLite in every measured payload/concurrency combination;
- post-reopen and mixed results also beat SQLite in every measured combination.

This is sufficient evidence to start an isolated production-provider implementation behind the existing InterfaceDB storage abstraction.

## Remaining Production Boundary

The prototype is not a complete production database provider.

Completed in the hardened prototype: bounded stripe queues with overload policy and telemetry, streaming checkpoint creation/loading, and bounded immutable checkpoint-generation retention.

Remaining work is:

1. Add automatic platform/device durability selection, bare-metal Linux measurements, and physical power-cut testing.
2. Add incremental compaction, tombstone/obsolete-version reclamation, disk-capacity backpressure, and safe reader-handle retirement.
3. Persist exact, range, RadixForge prefix/suffix, and contains index locators with versioned checkpoint/replay semantics.
4. Add cold-cache and data-larger-than-RAM read tests, long sustained mixed-load tests, and multi-process reader/writer tests.
5. Integrate encryption, backup consistency points, format migration, integrity scanning, and repair diagnostics.
6. Implement the provider behind `IEnvelopeRepository<TContract>` and rerun the public InterfaceDB production gate without changing repository/query semantics.
7. Add a public pipelined non-atomic `SaveManyAsync` path. Atomic multi-object commit is now a separate implemented prototype API, but still requires repository integration, coordinator-journal recycling, and power-loss qualification.

The next engineering slice is incremental compaction and disk-capacity control. Without reclamation, a healthy append-only engine eventually becomes an operational failure regardless of its benchmark speed.
