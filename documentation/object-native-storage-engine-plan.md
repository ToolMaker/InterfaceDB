# Object-Native Storage Engine Research And Plan

## Decision

InterfaceDB should prototype a second storage provider built as a **segmented, append-only object log with a primary key-to-address table and checkpointed secondary indexes**.

It should not replace the file-system provider yet, and it should not be described as a generally faster SQLite. The credible claim is narrower:

> For an embedded, single-process object repository whose normal operations are independent object saves, exact-key reads, and InterfaceDB-indexed queries, an object-native log can avoid SQL, row, B-tree-page, per-object-file, and double-WAL work.

That specialization gives InterfaceDB a credible route to lower durable-write cost, faster exact reads, and much faster reopen than the current file-per-object provider. A performance claim is accepted only after the admission benchmarks and crash tests in this document pass.

Research date: 2026-08-23.

## What “Beat SQLite” Can Honestly Mean

InterfaceDB can target SQLite on the workload InterfaceDB actually serves:

- one awaited object save per public operation;
- complete C# object materialization on reads;
- exact-key, range, prefix, suffix, contains, and conjunction queries through declared indexes;
- one embedded application process with many threads;
- warm and cold reopen;
- strict durability and explicitly non-durable performance modes measured separately.

It cannot honestly claim to beat SQLite at arbitrary SQL, joins, multi-table transactions, concurrent processes, or every hardware and data distribution. A strict independent save still needs a storage barrier. Once that barrier dominates latency, software can reduce bytes and CPU work but cannot remove the device's persistence latency.

The historical gate below is the starting point. Its query rows use InterfaceDB
`StrongObject`: startup deserialization during the index rebuild populated the
InterfaceDB object cache before timing, while SQLite still mapped complete objects
from rows. The query numbers are
therefore hot-cache application results, not canonical fresh storage-to-object rankings.
Use the gate's `MetadataOnly` profile for that comparison. The standalone startup row
includes InterfaceDB object deserialization for index reconstruction and ends before
a separately issued result query; SQLite only opens a connection in that row.

| Operation | InterfaceDB current | SQLite direct | SQLite + Dapper |
|---|---:|---:|---:|
| Durable independent object write, p50 | 5.626 ms | 3.019 ms | 3.094 ms |
| `StrongObject` hot-cache exact query, p50 | 0.342 ms | 0.025 ms | 0.030 ms |
| `StrongObject` hot-cache range query, p50 | 0.161 ms | 0.212 ms | 0.185 ms |
| `StrongObject` hot-cache broad prefix query, p50 | 1.326 ms | 1.754 ms | 1.505 ms |
| Provider-specific startup, 1,000 records, before separate query | 304.289 ms (object deserialization plus index rebuild) | 8.241 ms (connection open only) | 6.673 ms (connection open only) |

The earlier low-level probe already put a reused append handle in SQLite's physical cost class. The remaining question is whether that advantage survives serialization, recovery metadata, index publication, and the public InterfaceDB API.

## Current InterfaceDB Diagnosis

The current file provider gives every object an independently visible file and a recoverable cross-process publication protocol. In Durable mode a save coordinates through writer and payload locks, writes a proposal, and publishes it through renames. Startup enumerates object files, constructs envelope skeletons, and rebuilds in-memory indexes. Those are useful file-provider semantics, but they are the wrong physical unit for database-like performance.

The search indexes were not the cause of the durable write gap: disabling them changed write time by less than normal storage variance. The durable file protocol was reduced from 15.299 ms to 5.626 ms p50 without removing its tested guarantees. Further substantial improvement requires a different physical provider, not deleting safety steps from the file provider.

The existing abstraction is usable but has two file-oriented edges:

- `IEnvelopeRepository<TContract>` is the correct provider boundary.
- `IReaderAndWriter<TContract>` can remain as the low-level payload facade.
- `IObjectEnvelope.Location` is already documented as a general location descriptor, so a journal locator can use a provider URI such as `idb-log://segment/offset`.
- `ContractRepositoryType` needs a new provider value; the file-system value and behavior must remain available.
- File-only `StoragePolicy` settings must be separated from new journal settings instead of being silently ignored.

## Lessons From Existing Engines

These engines are not being classified as right or wrong in general. Their tradeoffs are evaluated against InterfaceDB's narrower object workload.

| Engine | What it gets right | Cost that InterfaceDB should avoid |
|---|---|---|
| SQLite | Sequential WAL, a WAL index, checksummed frames, one writer with concurrent readers, and bounded checkpointing. | Tables and indexes are page B-trees. WAL records page images, and checkpoints must transfer pages back into the database. This is excellent general embedded SQL machinery but duplicates work when InterfaceDB already owns object serialization and secondary indexes. |
| PostgreSQL | WAL-before-data, group commit, deferred data-page writes, mature recovery, and MVCC snapshots. | Server processes, 8 KiB pages, tuple/MVCC metadata, full-page images, vacuum, and general transactions are outside the embedded object-store target. |
| LMDB | One writer, lock-free readers, copy-on-write snapshots, a reader epoch table, and two alternating valid meta pages. | Copy-on-write B+tree updates and page reclamation optimize read-heavy ordered storage. Long readers retain old pages; write throughput is intentionally traded for reads. A pure append-only predecessor wasted more than 99% of its pages after only a few hundred updates, demonstrating why reclamation is mandatory. |
| RocksDB | Sequential WAL/memtable ingestion, immutable sorted files, Bloom/index blocks, and explicit backpressure. | LSM read amplification, write amplification, compaction tuning, and write stalls solve a much broader sorted-key problem than InterfaceDB needs. Existing InterfaceDB range/radix indexes would be duplicated. |
| FASTER / Tsavorite | A compact hash-to-log-address index, a record-oriented hybrid log, hot-set locality, checkpoints, and epoch-based safe reclamation. | Its latch-free concurrency, in-place mutable region, object-log split, checkpoint state machine, and pending I/O model are powerful but substantially more complex than InterfaceDB's current semantics require. Tsavorite is a benchmark control and source of design ideas, not the initial dependency. |
| Voron | A single writer, WAL integrity checks, transaction merging, immutable reader views, journal recycling, compression, and careful encrypted recovery. | It is a complete page-based storage engine with scratch files, page translation, native platform I/O, and a separate journal/data lifecycle. InterfaceDB can avoid the second durable copy by making its object log the primary data. |
| LiteDB | Demonstrates that a managed C# embedded engine can implement pages, a WAL index, recovery, and materialized documents. | Its commit path appends whole dirty pages and checkpoint copies confirmed pages to the data file under an exclusive operation. Repeating that structure would not exploit InterfaceDB's opaque serialized objects. |

Primary references:

- [SQLite WAL design](https://www.sqlite.org/wal.html) and [database file format](https://www.sqlite.org/fileformat.html)
- [PostgreSQL WAL](https://www.postgresql.org/docs/current/wal-intro.html), [WAL settings and group commit](https://www.postgresql.org/docs/current/runtime-config-wal.html), [page layout](https://www.postgresql.org/docs/current/storage-page-layout.html), and [vacuum](https://www.postgresql.org/docs/current/routine-vacuuming.html)
- [LMDB design paper](https://www.openldap.org/pub/hyc/20111010LDAPCon-MDB.html) and [LMDB source API](https://github.com/openldap/openldap/blob/master/libraries/liblmdb/lmdb.h)
- [RocksDB overview](https://github.com/facebook/rocksdb/wiki/RocksDB-Overview), [leveled compaction](https://github.com/facebook/rocksdb/wiki/Leveled-Compaction), and [write stalls](https://github.com/facebook/rocksdb/wiki/Write-Stalls)
- [FASTER paper](https://www.microsoft.com/en-us/research/uploads/prod/2018/03/faster-sigmod18.pdf), [FASTER recovery basics](https://github.com/microsoft/FASTER/blob/main/docs/_docs/20-fasterkv-basics.md), [current Garnet/Tsavorite repository](https://github.com/microsoft/Garnet), and [Tsavorite compaction source](https://github.com/microsoft/garnet/blob/main/libs/storage/Tsavorite/cs/src/core/Compaction/TsavoriteCompaction.cs)
- [Voron storage-engine design](https://docs.ravendb.net/7.2/server/storage/storage-engine), [Voron journal writer source](https://github.com/ravendb/ravendb/blob/v7.2/src/Voron/Impl/Journal/JournalWriter.cs), and [journal recovery source](https://github.com/ravendb/ravendb/blob/v7.2/src/Voron/Impl/Journal/JournalReader.cs)
- [LiteDB repository](https://github.com/litedb-org/LiteDB), [managed WAL write path](https://github.com/litedb-org/LiteDB/blob/dev/LiteDB/Engine/Disk/DiskService.cs), and [WAL checkpoint path](https://github.com/litedb-org/LiteDB/blob/dev/LiteDB/Engine/Services/WalIndexService.cs)

## Candidate Decision Matrix

| Candidate | Durable small writes | Exact reads | Reopen | Range/prefix fit | Complexity | Decision |
|---|---|---|---|---|---|---|
| Clone SQLite page B-tree + WAL | Good | Good | Excellent | Duplicates existing indexes | Very high | Reject |
| LMDB-style copy-on-write B+tree | Moderate | Excellent | Excellent | Good | High | Reject as primary |
| RocksDB-style LSM | Excellent ingestion | Read amplification | Good | Good | Very high | Reject as primary |
| Embed Tsavorite | Potentially excellent | Excellent | Checkpoint-dependent | Requires separate InterfaceDB indexes | High integration risk | Benchmark control |
| Segmented object log + address table | Potentially excellent | Excellent | Excellent with checkpoints | Directly reuses InterfaceDB indexes | Moderate and bounded | Prototype |

## Proposed Engine

Working architecture name: **InterfaceDB Object Log**. This is a provider name, not a public package or branding decision.

### 1. The Log Is The Database

There is no separate WAL followed by a second mandatory data-page write. A committed object frame in an immutable segment is the primary durable representation. Updates append a newer frame; deletes append a tombstone.

Suggested directory layout:

```text
database-root/
  manifest.a
  manifest.b
  writer.lock
  segments/
    0000000000000001.idbseg
    0000000000000002.idbseg
    active.idbseg
  checkpoints/
    0000000000004200.catalog
    0000000000004200.indexes
```

The active segment is held open for the engine lifetime. Sealed segments are immutable. Segment rotation is based on measured size and recovery limits, not object count.

File and directory durability must sit behind a small `IDurableFileSystem` boundary. It must define the exact Windows and Linux operations for file flush, directory-entry flush, atomic replace, safe rename, and file creation. `FileStream.Flush(true)` alone does not make a newly created directory entry portable crash-safe. The next segment is created, its header is flushed, and its existence is durably registered before any frame written to it can be acknowledged.

Each normal data segment links to its predecessor by ID and header hash. Rotation writes a sealed-next-segment marker to the old segment and registers the new header before use. This lets recovery discover a valid consecutive segment even when its newest checkpoint predates the rotation. Compaction outputs are a different record kind and become active only through a valid manifest, so an orphan compaction file can never be mistaken for acknowledged foreground data.

### 2. Record Format

Each segment starts with an aligned, checksummed header containing format version, segment ID, generation, creation information, and required encryption key IDs.

Each variable-length frame is aligned and contains:

- frame magic, format version, flags, and total lengths;
- 64-bit monotonically increasing log sequence number (LSN);
- contract/type identifiers and schema version;
- stable object key hash plus exact key bytes;
- object envelope metadata;
- normalized secondary-index projection block;
- serialized object bytes or tombstone;
- CRC32C over the stored frame (ciphertext when encrypted);
- a footer repeating critical length/LSN information and a commit marker.

The duplicated footer and checksum make a torn or partial tail detectable. Recovery accepts only structurally valid, checksummed, committed frames and stops at the first invalid tail frame. Hardware-accelerated CRC is for accidental corruption and tear detection; authenticated encryption remains responsible for tamper detection.

An invalid frame is treated as an incomplete tail only when it is at the active end and no later bytes can represent an acknowledged frame. Invalid data inside a sealed segment is corruption: normal read-write open fails and reports the exact segment/offset. A separate salvage tool may resynchronize and copy later valid records, but the production engine must never silently discard an interior region.

The first implementation should self-commit one object per frame because that matches InterfaceDB's public semantics. The format may reserve a future transaction/commit record kind, but public multi-object transactions are not a version-one requirement.

### 3. Write And Commit Path

One engine instance owns a single writer queue and one append handle per active segment.

For each save:

1. Serialize the envelope and compute index projections before entering the writer's critical path.
2. Preflight memory needed to publish the primary and index state, so an allocation failure does not occur after durable commit.
3. Assign the LSN and append one complete frame.
4. In Durable mode, flush the append handle to stable storage.
5. Publish the new address and index state in memory.
6. Complete the caller and then raise notifications.

An acknowledged Durable save therefore means the frame passed the stable-storage barrier. If a process dies after the flush but before returning success, recovery may expose the record; that is the standard unavoidable “outcome unknown to the caller” boundary of durable systems.

When multiple saves are already concurrently queued, the writer can drain them into one contiguous write and one flush, then complete all callers. That is legitimate group commit: each public save is still independently awaited and durable when it returns. The sequential latency benchmark must not use batching, and any artificial group delay must be reported separately.

Performance mode appends without a per-save stable-storage flush and must continue to state that acknowledged recent records can be lost after an OS or power failure.

Cancellation is honored before a prepared record enters commit. Once append begins, cancellation cannot promise that the object was not committed. An append or flush failure puts the writer into a faulted state; it does not continue beyond an uncertain tail. Reads of already committed records may continue, but new writes require successful recovery/reopen.

Phase 0 must compare the supported OS primitives—buffered append plus durable flush, write-through, and any correctly aligned unbuffered path—because their semantics and latency differ by filesystem and platform. The chosen implementation is the fastest one that passes the same power-loss contract, not simply the fastest API call.

### 4. Primary Address Table And Reads

The primary in-memory structure maps an exact storage key to:

```text
(segment ID, offset, frame length, LSN, runtime type, schema version)
```

Expected exact lookup is O(1). The initial correctness implementation should use a profiled concurrent dictionary rather than inventing a lock-free table immediately. The production checkpoint design is two-level:

1. an immutable checkpoint-backed hash table containing the stable key/address population; and
2. a mutable in-memory delta containing inserts, updates, and tombstones since that checkpoint.

Lookup checks the delta first and then the stable table. The stable table can initially be materialized into a dictionary; if that misses the reopen or memory gate, its fixed binary buckets should be memory-mapped or paged lazily. A cache-line-sized open-addressed layout can store a fingerprint and compact record address, following the useful part of FASTER's index design while preserving exact key verification on collisions. This makes mapping the checkpoint cheap while retaining O(1) lookup and avoids allocating a managed string/dictionary node for every stable key at open.

Reads capture one immutable record address, use `RandomAccess.ReadAsync` or a measured mapped-segment implementation, validate the frame, and deserialize directly from pooled memory. Sealed segments allow lock-free physical reads. Memory mapping is a benchmark candidate, not a default assumption: it can reduce copying but complicates Windows file deletion, address-space management, and corruption boundaries.

Strong/weak object caching remains above the storage engine. Storage benchmarks must also run `MetadataOnly` so an InterfaceDB cache hit is not compared with a fresh SQLite materialization.

### 5. Secondary Indexes Are Derived, But Recoverable

Exact, range, prefix, suffix, and contains indexes remain the responsibility of InterfaceDB's index layer. The object frame stores a compact normalized projection of the indexed values that were used for that object version.

This gives three benefits:

- commit and index truth share one LSN;
- recovery can rebuild or update indexes without deserializing full objects;
- a checkpoint can persist index state at a named LSN and replay only the tail.

The record is authoritative. An in-memory index is never independently “more committed” than the record. If a process dies after the frame commit but before index publication, tail replay repairs the index.

The checkpoint interface should support native immutable snapshots plus mutable deltas for InterfaceDB and RadixForge indexes. Suitable persistent forms are:

- exact values: immutable hash buckets and posting lists;
- range values: sorted value/record-ID arrays suitable for binary search;
- prefix/suffix: serialized RadixForge nodes and posting lists;
- contains: compressed token/gram posting lists.

Until each index has a stable snapshot codec, the checkpoint can persist projection rows and rebuild from those rows without object materialization. This is less ideal than loading native index nodes but still removes per-object file discovery and full payload decoding. The production reopen gate includes first-query readiness, so projection-row rebuilding cannot be hidden by reporting only the repository constructor time.

Index projections have security consequences. Equality on protected fields can use keyed tokens. Prefix, suffix, range, and contains indexes inherently reveal ordering or token structure; they must either be disallowed for protected fields or require an explicit leakage-aware option. Plain indexed values must not silently bypass current field-encryption expectations.

### 6. Checkpoint And Reopen

A checkpoint records an exact `checkpointLSN` and contains:

- the primary key-to-record-address catalog;
- runtime type/schema metadata;
- secondary-index snapshots or projection rows;
- the segment manifest and encryption-key dependencies;
- strong checksums and format versions.

Checkpoint publication uses two alternating manifest slots, inspired by LMDB's double meta pages:

1. Write and flush new checkpoint files.
2. Write the inactive manifest with the new generation and hashes.
3. Flush the manifest.
4. Recovery selects the highest valid manifest generation.

Opening the database maps or loads the latest valid checkpoint and replays only committed frames with an LSN greater than `checkpointLSN` into mutable deltas. Checkpoint cadence must bound both replay bytes and replay time. If the newest checkpoint is corrupt, recovery falls back to the prior valid generation; a full segment scan is the final recovery path, not the normal startup path.

Startup is not magically O(1): a fully materialized catalog is O(N) in stable keys plus O(delta) replay. The mapped immutable-table design changes eager startup work toward O(number of mapped pages touched + delta), leaving stable buckets to fault in on demand. Both “open completed” and “first exact/range/prefix query completed” must be measured so lazy work is visible.

Checkpoint creation need not copy a live in-memory dictionary under a long writer pause. At the checkpoint boundary the writer seals/rotates the active segment and records a boundary LSN. A background checkpoint then merges the previous checkpoint with the now-immutable segment/projection deltas up to that LSN while foreground writes continue in the new segment. Manifest publication happens only after that merged checkpoint is complete and flushed.

Object hydration remains lazy by default. `HydrateOnInitialize` is a cache warmup option, not a requirement for index correctness.

### 7. Segment Compaction

Append-only storage without reclamation is not production-ready. Compaction operates on sealed segments and copies only frames still referenced by the current primary address table.

Safe compaction protocol:

1. Select old sealed segments by measured live-byte ratio and available disk space.
2. Capture a manifest generation and address-table view.
3. Copy live frames to a new compacted segment, preserving their original LSNs; copy encrypted payload bytes without decrypting when possible.
4. Flush and validate the compacted segment.
5. Build a relocated catalog generation (or an equivalent checksummed relocation table) whose addresses point into the compacted output.
6. Briefly coordinate with the writer to revalidate each relocation, publish new in-memory addresses only where the old address is still current, and publish a new manifest generation that atomically names both the relocated catalog and the compacted segment set.
7. Keep old segments until all readers pinned to the old manifest generation have exited.
8. Delete retired segments later and tolerate orphan output from a crash before manifest publication.

Concurrent updates are safe because they receive higher LSNs in newer segments. Recovery chooses the highest committed LSN for a key, not whichever file happens to be scanned last.

A tombstone may be discarded only when every older segment that could contain the deleted value is excluded by the same durable manifest. This prevents deleted objects from being resurrected after compaction or fallback recovery.

The relocated catalog rule is mandatory: a manifest must never remove a segment while its selected checkpoint still contains a live address into that segment. Readers that captured an old address remain safe because the old manifest generation and segments stay pinned until their read epoch exits.

Compaction needs explicit backpressure and disk-space rules. It must never start work that cannot finish with the configured free-space reserve, and foreground p95/p99 latency under compaction is a release gate.

### 8. Concurrency Contract

Version one should make the concurrency boundary explicit:

- many reader threads;
- one process-wide writer queue per database root;
- one read-write process holding an OS-backed lifetime lease;
- no silent support for two independent read-write processes;
- optional read-only secondary-process support only after tail visibility and manifest pinning are proven.

Multiple repositories inside the same application must share the engine instance for a root. This retains the embedded, no-connection-string model while avoiding a distributed cache-coherency problem inside a local library.

The current file provider remains the choice when independently inspectable files or cross-process file-change semantics matter more than database-like performance. A future cross-process journal mode would need a shared writer mutex, committed-tail notification, per-process index tailing, and reader epochs; it is not a small toggle.

### 9. Migrations, Encryption, Deletes, And Backup

- A migration appends a new higher-version frame. The old frame remains unreachable and is reclaimed by compaction.
- Lazy encryption-key rotation appends a new encrypted frame. A key cannot be retired until no active manifest segment or checkpoint references it.
- Delete is a durable tombstone, not guaranteed physical erasure. Compaction removes logical remnants, but secure overwrite cannot be promised on SSDs.
- Backup must capture one durable manifest generation and every referenced checkpoint/segment. The simplest first safe operation seals the active segment before copying.
- Restore validation must verify all hashes, segment IDs, LSN ordering, serializer/schema availability, and required encryption keys before opening read-write.

## Expected Performance Mechanism

| Cost | Current file provider | SQLite | Proposed object log |
|---|---|---|---|
| Durable data unit | One proposal/canonical object file | WAL page frame(s) | One variable-length object frame |
| Handle lifecycle | Several per-object filesystem operations | Reused database/WAL handles | Reused active-segment handle |
| Stable barriers | Publication protocol dependent | WAL commit barrier | One append barrier |
| Secondary indexes | Rebuilt from objects | Persistent B-trees | Checkpoint + projection-tail replay |
| Exact address | File/envelope cache | B-tree traversal | O(1) address table |
| Update | Replace/publish object file | Page/WAL updates | Append newer frame |
| Reopen | Enumerate files and rebuild indexes | Open persistent pages/indexes | Load checkpoint + bounded tail replay |
| Reclamation | File deletion | Checkpoint/freelist | Segment compaction |

The strongest expected gains are:

1. **Durable writes:** one reused sequential append, one checksum, and one flush; no per-object locks, proposal names, renames, B-tree page modification, or second data copy.
2. **Concurrent durable throughput:** ready concurrent requests share one physical flush without changing their durability result.
3. **Exact reads:** direct key-to-address lookup and immutable segment read.
4. **Reopen:** load a compact catalog/index checkpoint and replay a bounded metadata tail instead of enumerating and hydrating object files.
5. **Indexed queries:** retain current range/prefix strengths while removing full-object index rebuilds.

### Complexity Targets

Let `B` be serialized object bytes, `P` be encoded index-projection bytes, `N` be stable live keys, `D` be records since the checkpoint, `R` be returned objects, and `L` be live bytes in compacted segments.

| Operation | Target CPU/I/O complexity |
|---|---|
| Save one object | O(B + P), one sequential append, and one strict flush |
| Exact lookup | Expected O(1) address probes + O(B) read/deserialization |
| Delete | Expected O(1) address/index update + one tombstone append/flush |
| Open with materialized checkpoint | O(N + D), without object deserialization |
| Open with mapped stable checkpoint | O(mapped pages actually touched + D) eager work |
| Indexed query | Existing index complexity + O(R) address reads/materializations |
| Full enumeration | O(N + D + total returned bytes) |
| Checkpoint | O(N + D) background merge/write for a full consolidated generation |
| Segment compaction | O(bytes scanned + L copied) |

The design improves constants and removes redundant persistence work; it does not change the fundamental O(B) cost of storing or materializing an object, nor the O(live data) cost of eventually reclaiming an append-only history.

## Implementation Roadmap And Exit Gates

### Phase 0 — Storage Admission Probe

Status (2026-08-23): **complete; no-go under the approved explicit-`FlushToDisk` admission rule.** The isolated prototype, deterministic fault/recovery suite, Windows measurements, virtualized Linux measurements, and decision are recorded in [Object-Native Storage Phase 0 Results](object-log-phase-zero-results.md). That exact design was not productionized. Later phases consciously revised the physical strategy to striped write-through/group commit and ultimately produced the separately qualified SegmentCore provider.

Build a disposable low-level prototype outside the production provider:

- one reusable append handle;
- proposed frame header/footer and CRC32C;
- one frame per independent object save;
- `FlushToDisk` in Durable mode;
- segment creation/rotation through the cross-platform durable-filesystem boundary;
- forward recovery over deliberately torn tails;
- current file provider, SQLite direct, SQLite+Dapper, and Tsavorite controls.

Admission gate:

- On both Windows and Linux NVMe, strict sequential p50 must be at least 15% faster than SQLite direct `FULL`, and p95 must not be slower.
- If the storage barrier makes that impossible, proceed only if public-API exact reads/reopen and concurrent strict throughput demonstrate a material total-workload advantage. Do not market faster durable single saves.
- Every acknowledged frame must survive process termination; every truncated/corrupted suffix must recover to the last valid committed LSN.

### Phase 1 — Minimal Correct Engine

- Segment lifecycle and rotation.
- Single-writer queue and natural group commit for already-concurrent requests.
- Primary address table.
- Put/get/delete and tombstones.
- Double-manifest publication.
- Deterministic recovery and format-version rejection.
- Fault injection at every write/publication boundary.
- Writer-fault behavior after disk-full, short-write, canceled I/O, and flush failure.

Exit gate: no lost acknowledged writes, no visible unacknowledged partial writes, and no resurrected deletes across at least 10,000 randomized kill/truncate/corrupt recovery cycles.

### Phase 2 — InterfaceDB Provider Integration

- Add a journal/object-log repository type behind `IEnvelopeRepository<TContract>`.
- Reuse the current serializer, polymorphic runtime-type handling, migrations, object cache, and notifications.
- Publish notifications only after durable storage and in-memory visibility.
- Add provider-specific options rather than extending file-only switches.
- Keep the file provider and its tests unchanged.

Exit gate: full InterfaceDB integration suite plus differential tests against an in-memory oracle and the file provider.

### Phase 3 — Persistent Catalog And Index Checkpoints

- Catalog checkpoint at an exact LSN.
- Immutable stable catalog plus mutable post-checkpoint delta.
- Index projection codec and tail replay.
- Native snapshot/restore hooks for exact, range, RadixForge prefix/suffix, and contains indexes.
- Mapped/paged snapshot implementations when materialized loading misses the reopen gate.
- Bounded checkpoint cadence and fallback to a prior generation.
- Lazy hydration.

Exit gate: 10k, 100k, and 1m reopen tests; corrupt-newest-checkpoint fallback; public query results identical before and after restart.

### Phase 4 — Compaction, Backup, And Security Completion

- Live-ratio segment cleaner and generation-pinned readers.
- Relocated catalog publication in the same manifest generation as compacted segments.
- Safe tombstone collection.
- Disk-space reservation and backpressure.
- Consistent backup/restore.
- Encryption-key dependency tracking and rotation completion.
- Explicit protected-index policy.

Exit gate: update-heavy 24-hour soak, crash during every compaction phase, no stale-key resurrection, bounded disk amplification, and successful restore on another machine.

### Phase 5 — Production Hardening

- .NET 10 Windows and Linux storage backends.
- Corruption diagnostics and read-only salvage tooling.
- Metrics for tail length, checkpoint age, live ratio, compaction debt, flush latency, bytes written, and recovery source.
- Format compatibility/migration tests across released versions.
- Optional specialized mutable-delta address table only if profiling proves the concurrent dictionary is material.
- Multi-process capability remains a separate project and release claim.

Exit gate: all benchmark gates below, no unresolved severity-one recovery defects, documented operational limits, and a rehearsed backward-compatible upgrade/rollback procedure.

## Benchmark And Correctness Matrix

All SQL comparisons must include complete row-to-object materialization and result validation. Every InterfaceDB mode must state whether the object cache, OS cache, encryption, notifications, and durability barrier are enabled.

### Data Matrix

- Counts: 1k quick, 10k, 100k, and 1m.
- Payloads: approximately 128 B, 1 KiB, 16 KiB, and 1 MiB; compressible and incompressible.
- Key distributions: sequential, random, shared prefixes, and adversarial hash collisions.
- Write mixes: insert-only, 50% updates, 95% updates/hot set, and delete/reinsert.
- Query selectivity: one item, 0.1%, 1%, 10%, and full-result scans.
- Cache states: strong object, metadata only with warm OS cache, and cold process/OS cache where reproducible.

### Measured Operations

- strict sequential independent save latency, p50/p95/p99;
- strict concurrent saves at concurrency 2/4/8/16, including group size and flush count;
- performance-mode saves, reported separately;
- exact read through storage API and through public query API;
- range, prefix, suffix, contains, and multi-index conjunction;
- cold and warm reopen plus bytes replayed;
- first exact, range, prefix, suffix, and contains query after reopen;
- compaction throughput and foreground latency impact;
- backup/restore and recovery time;
- CPU, allocations, GC, logical bytes, physical bytes, flushes, file operations, and peak memory.

### Required Baselines

- current InterfaceDB file provider;
- SQLite direct and SQLite+Dapper with equivalent durability and one transaction per object save;
- SQLite batch transaction only as a separately labelled ceiling, never as the independent-save comparison;
- Tsavorite point-store prototype;
- raw append+flush physical floor.

### Production Performance Gates

The provider is not the default and no “faster than SQLite” claim is published unless:

- strict independent save p50 is at least 15% faster than SQLite direct `FULL` on both supported OS test machines, or the claim explicitly excludes this operation;
- strict concurrent throughput is at least 1.5x SQLite at one measured concurrency without weakening durability;
- exact metadata-only object read is faster end-to-end, not merely faster before deserialization;
- warm reopen is no slower than SQLite at 100k objects and at least 10x faster than the current file provider;
- existing InterfaceDB range/prefix strengths regress by no more than 10%;
- steady-state physical bytes after compaction are at most 1.5x live serialized bytes for the standard update workload;
- compaction increases foreground p99 by less than 2x and never causes an unbounded write stall;
- crash/recovery and corruption gates pass with zero acknowledged-data loss.

Results must be published even when a gate fails. Hardware, filesystem, runtime, engine settings, cache state, and exact object-materialization contract are part of every result.

## Principal Risks

1. **Flush latency sets a hard floor.** The custom engine may reduce CPU and write volume but fail to beat SQLite's strict sequential p50 on some devices.
2. **Compaction is the real production cost.** Append-only prototypes look excellent before update-heavy reclamation is included.
3. **Persistent secondary indexes can become a second database.** They must remain derived from one authoritative LSN stream and checkpoint atomically.
4. **Index projections can leak protected values.** Security behavior must be designed before persistence, not patched afterward.
5. **Cross-process behavior is not free.** A single read-write process is an intentional initial capability boundary.
6. **Large objects distort segment cleaning.** Segment sizes, large-frame/chunk handling, and compaction selection require dedicated tests.
7. **Managed memory can erase I/O gains.** Pooling, serializer allocations, address-table size, and object cache behavior must be profiled end-to-end.
8. **A new format creates a long compatibility obligation.** Versioning, salvage, backup, and migration are release-blocking features.

## Go / No-Go Recommendation

**Go for Phase 0 and Phase 1 only.** The architecture is a strong fit and directly attacks the measured bottleneck. It also preserves InterfaceDB's main principle: application code depends on repository abstractions while storage implementations can vary.

**Do not yet commit to replacing the file provider or publishing a universal SQLite performance claim.** The project earns that decision after the strict append probe, recovery proof, checkpointed reopen, and compaction-under-load gates pass.

If the append probe cannot beat SQLite's strict write path, the work is still useful only if the integrated provider wins materially on the combined InterfaceDB workload. If neither condition holds, stop and use the current file provider or a proven embedded engine rather than maintaining a custom database for novelty.
