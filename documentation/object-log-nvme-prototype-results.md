# Object-Native Storage NVMe Prototype Results

## Decision

The NVMe-oriented follow-up is a **go for a minimal production-engine phase, but not yet a production-provider release**.

The original single-writer, explicit-flush probe remains a no-go for isolated saves. The new evidence changes the broader architecture decision:

- On native Windows/NVMe, stable hash-partitioned write-through logs scale from roughly 8,000 durable object saves/s with one caller to roughly 34,000 saves/s with concurrent callers.
- On virtualized Linux, per-record write-through does not scale. One explicitly flushed log with group commit is the winning tested policy and reaches roughly 3,600 saves/s at concurrency 32.
- All deterministic prototype tests and all representative forced-process-termination cycles recovered every acknowledged record.

The provider therefore needs a platform- and device-qualified durability policy. It must not assume that more files or one persistence primitive wins everywhere.

Research and measurement date: 2026-08-24.

## Architecture Tested

The follow-up keeps the Phase 0 checksummed, self-committing frame and segment-chain format and adds:

- 1, 2, 4, 8, or 16 independent physical stripes;
- stable FNV-1a routing over the exact object key, so an object's updates and tombstone always return to the same stripe after reopen;
- one active append queue and segment chain per stripe;
- concurrent writes across stripes;
- explicit-flush group commit, where all queued callers complete only after their affected stripe is flushed;
- a Windows write-through policy, where each successful record write is the durability operation;
- a checksummed manifest that fixes the stripe count for the store;
- aggregate recovery across all stripe chains;
- bounded batch size, segment rotation, corruption detection, tail repair, and single-writer leases inherited from the Phase 0 probe.

Logical query indexes are deliberately separate from physical stripes. Hash stripes balance writes. Exact, range, radix prefix/suffix, and contains indexes should store object locators of the form `(stripe, segment, offset, length, version)`.

## Fairness Contract

The confirmation run used:

- one public save and one acknowledgement per object;
- no explicit multi-object transaction for any engine;
- complete `PhaseZeroCodec` serialization inside each custom timed operation;
- complete SQLite object binding and one `FULL` WAL autocommit per object;
- no secondary indexes in SQLite or InterfaceDB for this storage-layer comparison;
- fresh stores per iteration, verification by materialized count and checksum, and engine cases interleaved with a deterministic shuffle;
- 500 measured objects x 5 fresh-store iterations per Windows confirmation metric;
- concurrency 1, 4, 16, and 64 and payloads of 512 B, 4 KiB, and 64 KiB.

Group commit does not turn multiple InterfaceDB calls into one public transaction. Concurrent callers share a physical persistence barrier, but each call returns only after the records covered by that barrier are durable. This is the same class of internal optimization used by mature databases.

## Native Windows/NVMe Confirmation

Environment:

- Samsung SSD 980 PRO 1 TB NVMe;
- NTFS, 512-byte logical sectors and 4 KiB physical sectors;
- Windows 10.0.19045;
- .NET 10.0.9;
- report: `artifacts/phase-one/windows-nvme-interleaved-confirmation-20260824.json`.

Representative interleaved confirmation metrics:

| Payload | Concurrency | Winning custom policy | Custom ops/s | Custom p50 | Custom p99 | SQLite FULL ops/s | Current file provider ops/s |
|---:|---:|---|---:|---:|---:|---:|---:|
| 512 B | 1 | 4 write-through stripes | 7,949 | 0.121 ms | 0.226 ms | 311 | 377 |
| 512 B | 16 | 16 write-through stripes | 30,543 | 0.422 ms | 1.337 ms | 293 | 711 |
| 512 B | 64 | 8 write-through stripes | 34,367 | 1.733 ms | 3.759 ms | 318 | 529 |
| 4 KiB | 1 | 4 write-through stripes | 7,361 | 0.122 ms | 0.271 ms | 305 | 268 |
| 4 KiB | 16 | 16 write-through stripes | 30,619 | 0.408 ms | 1.742 ms | 299 | 660 |
| 4 KiB | 64 | 16 write-through stripes | 31,056 | 1.855 ms | 4.296 ms | 272 | 627 |
| 64 KiB | 1 | 1 write-through stripe | 4,184 | 0.219 ms | 0.478 ms | 270 | 357 |
| 64 KiB | 16 | 16 write-through stripes | 17,379 | 0.779 ms | 2.583 ms | 257 | 660 |
| 64 KiB | 64 | 8 write-through stripes | 13,258 | 4.227 ms | 14.155 ms | 265 | 638 |

The earlier full sweep independently measured 34,340 saves/s for 512 B, 29,766 saves/s for 4 KiB, and 16,442 saves/s for 64 KiB at the best tested high-concurrency points. It also showed that explicit-flush stripes are counterproductive on Windows: splitting the queue reduces group size and issues competing file flushes. One explicit-flush stripe reached 9,386 saves/s at concurrency 64 for 512-byte objects; 8-16 write-through stripes reached more than 30,000 saves/s.

The long ordered sweep exposed SSD tail-latency and steady-state effects in engines measured late in the run. The interleaved confirmation is the primary comparison; the ordered sweep is retained as exploratory evidence and for its stripe-scaling curve.

## Virtualized Linux Evidence

Environment:

- Ubuntu 24.04.4 in Docker Desktop/WSL2;
- Docker's Linux filesystem backed by the same development NVMe;
- .NET 10.0.11;
- report: `artifacts/phase-one/linux-wsl2-nvme-baseline-20260824.json`.

For 512-byte objects:

| Concurrency | Winning custom policy | Custom ops/s | Custom p50 | Custom p99 | SQLite FULL ops/s | Current file provider ops/s |
|---:|---|---:|---:|---:|---:|---:|
| 1 | 1 explicit-flush stripe | 149 | 6.609 ms | 9.164 ms | 139 | 132 |
| 32 | 1 explicit-flush stripe with group commit | 3,574 | 7.494 ms | 15.361 ms | 135 | 1,316 |

Linux write-through maps to per-write synchronous behavior in this environment and was much slower under concurrency: 8 write-through stripes reached only about 545 saves/s at concurrency 32. This is useful kernel/filesystem evidence but is not a bare-metal Linux NVMe release gate.

## Correctness Evidence

- 18 managed prototype tests pass, including concurrent appends, deterministic routing across reopen, updates, tombstones, rotation, corruption rejection, every truncated final-frame position, publication/flush failures, writer exclusion, explicit group commit, and write-through recovery.
- Focused, full-sweep-selected, and interleaved-confirmation native-Windows write-through candidates passed 34/34 forced-process-termination cycles in total.
- Virtualized Linux explicit-flush recovery passed 5/5 forced-process-termination cycles.
- Recovery may expose valid records whose caller died before observing success. That outcome-unknown boundary is expected. No acknowledged record was absent or invalid.

Forced process termination is not sudden power loss. Before release, the selected policy must pass repeated power-cut testing on hardware that reports and honors write-through/flush behavior.

## Why The Windows Result Is Credible

`.NET FileOptions.WriteThrough` instructs the system to write through intermediate caches and go to disk. Microsoft also documents that write-through causes file data and metadata to be written immediately, and SQL Server opens database files with the corresponding `FILE_FLAG_WRITE_THROUGH` stable-storage policy.

Relevant platform references:

- [FileOptions.WriteThrough](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileoptions?view=net-10.0)
- [Windows file caching and write-through](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching)
- [SQL Server I/O fundamentals](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-storage-guide)
- [Windows storage write-cache capabilities](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddstor/ns-ntddstor-_storage_write_cache_property)
- [NVMe flush command semantics](https://learn.microsoft.com/en-us/windows/win32/api/nvme/ne-nvme-nvme_nvm_commands)

The application still cannot repair dishonest firmware or an unprotected controller cache. A production provider must query device capabilities, log the selected policy, provide a conservative explicit-flush fallback, and document the hardware contract.

## Production Boundary And Next Phase

This prototype proves the write-path opportunity. It does not yet provide a complete database replacement. Production work remains:

1. Add a bounded, backpressured queue and explicit overload behavior.
2. Add platform/device capability probing and an `Auto` durability policy: Windows-qualified write-through stripes, Linux-qualified group commit, conservative fallback otherwise.
3. Run programmable power-cut tests and bare-metal Linux NVMe tests.
4. Reduce allocation: the current codec/frame path allocates roughly 2.1 KiB for a 512-byte payload, 9.3 KiB for 4 KiB, and 133 KiB for 64 KiB.
5. Add a checkpointed primary key-to-address table and random-read benchmarks.
6. Persist/checkpoint InterfaceDB exact, range, RadixForge prefix/suffix, and contains indexes using `(stripe, segment, offset)` locators.
7. Add compaction, tombstone reclamation, disk-capacity backpressure, backup consistency points, encryption integration, format upgrades, repair tooling, and operational diagnostics.
8. Repeat end-to-end reads, updates, deletes, reopen, index queries, and mixed workloads against SQLite after those components exist.

The honest conclusion is: **the custom object-log write architecture has passed its performance admission on native Windows and under concurrent virtualized-Linux explicit group commit. InterfaceDB has not yet beaten SQLite as a complete database because reads, checkpoints, persistent indexes, compaction, and operations are still prototype gaps.**
