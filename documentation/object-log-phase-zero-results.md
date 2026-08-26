# Object-Native Storage Phase 0 Results

## Decision

**Phase 0 is complete and the proposed explicit-`FlushToDisk` engine is a no-go. Do not integrate this prototype into production InterfaceDB and do not begin Phase 1 under the current admission rule.**

The prototype is correct enough to answer the admission question, but it cannot be 15% faster than SQLite `FULL` when every independent object save must execute `RandomAccess.FlushToDisk`. On both tested platforms, the raw append control itself is already in SQLite's latency class. Frame parsing, CRC32C, one reused handle, and object encoding cannot recover performance that is consumed by the storage barrier.

At the time of this phase decision, the code was deliberately isolated in `StorageEngine.Prototypes`. The later NVMe, SegmentCore, transaction, recovery, and production-provider phases superseded that implementation status without changing this phase's no-go result for one explicit flush per independent append.

## What Was Built

- A reusable single-writer append handle with a process-local lease and an OS byte-range lease.
- CRC32C-framed put, delete, and terminal segment-seal records.
- Header and footer checksums plus an explicit commit marker.
- Durable segment publication through `MoveFileEx(MOVEFILE_WRITE_THROUGH)` on Windows and rename plus directory `fsync` on Linux.
- Chained segment rotation that publishes the next segment before sealing the current segment.
- Recovery that follows only the sealed segment chain, ignores unlinked orphan segments, truncates incomplete active tails, rejects committed-frame corruption, applies tombstones, and enforces contiguous LSNs.
- Explicit-flush Durable mode, OS write-through Durable mode, and separately labelled Performance mode.
- A permanently faulted writer state after uncertain write, flush, or segment-publication failure.
- A complete-object benchmark against the current InterfaceDB file provider, SQLite direct, SQLite+Dapper, Microsoft FASTER Log, and a raw append floor.

## Correctness Evidence

The Windows and Linux test runs both passed all 12 deterministic tests:

| Test family | Result |
|---|---|
| CRC32C standard vector | Pass |
| Durable append and materialized recovery | Pass |
| Every possible short final-frame truncation | Pass |
| Interior payload corruption rejection | Pass |
| Fully written final-frame corruption rejection | Pass |
| Linked rotation and orphan exclusion | Pass |
| Delete tombstone recovery | Pass |
| Explicit-flush, write-through, and performance-mode barriers | Pass |
| Injected flush failure faults writer | Pass |
| Injected segment-publication failure faults writer and preserves prior acknowledgement | Pass |
| Same-process writer exclusion; OS-backed lease acquisition on Windows/Linux | Pass |

The representative benchmark then ran 12 forced-termination cycles per platform with rotating 4 KiB segments. Every observed acknowledged object survived and decoded correctly:

| Platform | Passed cycles | Lost acknowledged objects | Recovery corruption |
|---|---:|---:|---:|
| Windows 10 | 12/12 | 0 | 0 |
| Ubuntu 24.04 container on WSL2 | 12/12 | 0 | 0 |

The deterministic truncation suite supplies torn-tail coverage. A normal process kill often leaves a complete additional unacknowledged frame because the write reaches the OS before the process is terminated; recovery may validly return more objects than the acknowledgement observer saw.

## Benchmark Contract

- .NET 10 only.
- 300 measured independent saves per iteration, seven fresh-store iterations, 2,100 samples per engine.
- Four warm-up objects per fresh store, excluded from latency samples but included in verification.
- Approximately 512 ASCII content bytes plus the remaining object fields.
- Complete object encoding is inside the timed append-log, raw-log, and FASTER operation.
- SQLite binds every object field, uses WAL and `synchronous=FULL`, creates no secondary indexes, and autocommits each object independently.
- InterfaceDB performs its normal complete object save with the passthrough index manager.
- No engine receives a multi-object transaction or amortized durability barrier.
- Every store is reopened/scanned or snapshotted outside the timed path, and count plus a complete-object checksum is validated.

## Measured Results

### Windows 10, .NET 10.0.9, Samsung 980 PRO NVMe

| Engine | Persistence path | p50 | p95 | Result versus SQLite direct |
|---|---|---:|---:|---|
| Raw reused append | `FlushToDisk` per object | 3.122 ms | 3.299 ms | Physical floor |
| Phase 0 object log | Explicit `FlushToDisk` | 3.049 ms | 4.600 ms | 2.9% faster p50; gate fail |
| Phase 0 object log | OS write-through | 0.124 ms | 0.207 ms | Informational alternative |
| Microsoft FASTER Log 2.6.5 | Per-entry checksum and fast commit | 0.435 ms | 0.617 ms | Architectural control |
| SQLite direct | WAL, `FULL`, one autocommit | 3.140 ms | 5.919 ms | Admission baseline |
| SQLite+Dapper | WAL, `FULL`, one autocommit | 3.172 ms | 3.405 ms | Mapping control |
| Current InterfaceDB file provider | Durable, no search indexes | 2.433 ms | 2.963 ms | Existing-provider control |
| Phase 0 object log | Performance, final flush outside timing | 0.014 ms | 0.040 ms | Not strict durability |

The approved p50 requirement was at least 15% faster than SQLite direct. The observed explicit-flush advantage was only 2.9%. The raw floor is only 0.6% below SQLite's median in this run; there is no 15% optimization envelope left in managed framing code.

Microsoft FASTER Log measured 0.435 ms p50 and 0.617 ms p95 using per-entry checksums, fast commit, and one `CommitAsync` per object. An earlier repeated run exposed a commit-file deletion race until outdated-commit deletion was disabled. Treat it as a useful architectural control, not an approved InterfaceDB dependency. There is no current standalone Tsavorite NuGet package equivalent to this control.

### Ubuntu 24.04, .NET 10.0.11, Docker Desktop on WSL2 ext4-backed storage

| Engine | Persistence path | p50 | p95 | Result versus SQLite direct |
|---|---|---:|---:|---|
| Raw reused append | `fsync` per object | 6.490 ms | 7.595 ms | Physical floor |
| Phase 0 object log | Explicit `fsync` | 6.570 ms | 8.481 ms | 1.5% faster p50, 2.0% slower p95; gate fail |
| Phase 0 object log | OS write-through | 6.788 ms | 9.511 ms | Gate fail |
| Microsoft FASTER Log 2.6.5 | Per-entry checksum and commit | 13.597 ms | 16.852 ms | Slower control |
| SQLite direct | WAL, `FULL`, one autocommit | 6.673 ms | 8.318 ms | Admission baseline |
| SQLite+Dapper | WAL, `FULL`, one autocommit | 7.169 ms | 8.675 ms | Mapping control |
| Current InterfaceDB file provider | Durable, no search indexes | 7.486 ms | 9.291 ms | Existing-provider control |
| Phase 0 object log | Performance, final flush outside timing | 0.004 ms | 0.031 ms | Not strict durability |

This is valid Linux-kernel/filesystem development evidence but is not a substitute for the plan's bare-metal Linux NVMe release gate. That distinction does not change the Phase 0 decision because Windows already failed the mandatory gate and virtualized Linux independently shows the same storage-barrier ceiling.

A full 2026-08-26 release rerun on the final source used seven 300-object iterations and 12 forced-termination cycles. Explicit flush measured 6.645 ms p50/8.259 ms p95 versus SQLite direct at 7.026 ms/8.601 ms: a 5.4% p50 and 4.0% p95 advantage, still below the historical 15% prototype admission threshold. All 12 crash cycles and all 91 SegmentCore tests passed. Raw append measured 6.419 ms p50, confirming again that the remaining strict-durability margin is in the virtualized storage barrier rather than managed framing. For the production provider, this 15% lead is now an advisory optimization target; correctness remains the release requirement. Report: `artifacts/phase-zero/linux-release-full-20260826.json`.

## Interpretation

1. **The frame design is not the bottleneck.** Windows framing adds roughly 0.0–0.1 ms around a roughly 3 ms raw persistence barrier. Linux shows the same relationship around a roughly 6.5 ms barrier.
2. **The original 15% target is physically unavailable under explicit per-object flush.** SQLite is already close to the raw append floor.
3. **Windows write-through is worth preserving as research, not as a cross-platform conclusion.** Microsoft documents write-through as stable-storage completion and SQL Server uses the same class of flag, but the Linux result has no comparable advantage and the approved Phase 0 contract explicitly named `FlushToDisk`.
4. **Performance mode is genuinely fast but changes the acknowledgement guarantee.** It can support an explicitly weaker policy, not replace Durable mode silently.
5. **A custom database is not justified by these results.** Continuing would add checkpointing, compaction, backup, encryption, repair, and format-compatibility obligations without first earning a durable-write advantage.

## Recommended Next Move

- Keep the current file provider for independent, inspectable, abstraction-first object files.
- Do not productionize `StorageEngine.Prototypes` or publish it as a package.
- If InterfaceDB needs database-style density and reopen speed, prototype a provider over a proven embedded engine behind `IEnvelopeRepository<TContract>` rather than owning a new storage engine.
- If the product explicitly accepts a weaker durability policy, retain the write-through/performance measurements as a separate policy discussion with power-loss testing on real Windows and Linux hardware.
- Return near-term focus to public-release readiness: Linux integration coverage, backup/restore and integrity tooling, soak tests, packaging, and clean-consumer validation.

Historical note: the later NVMe design changed the physical strategy enough to pass a separate admission gate. The resulting SegmentCore provider is not the rejected one-flush-per-object Phase 0 design.

## Reproduce

Native Windows or Linux:

```powershell
.\Scripts\Test-ObjectLogPhaseZero.ps1
```

Virtualized Linux through Docker Desktop:

```powershell
.\Scripts\Test-ObjectLogPhaseZero-LinuxDocker.ps1
```

Native Linux without PowerShell:

```bash
bash Scripts/test-object-log-phase-zero.sh
```

Primary durability references:

- [.NET `FileOptions.WriteThrough`](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileoptions?view=net-10.0)
- [Windows `CreateFile` write-through semantics](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea)
- [Linux `open(2)` synchronized-I/O semantics](https://www.man7.org/linux/man-pages/man2/open.2.html)
- [Microsoft FASTER Log commit and fast-commit semantics](https://microsoft.github.io/FASTER/docs/fasterlog-basics/)
