# SegmentCore Transactions And Performance

## Status

SegmentCore has store-local atomic multi-object transactions with two immutable per-store durability protocols: the participant-commit control path and the production-default mutation-bearing write-ahead log (WAL). Both provide explicit transaction handles, implicit one-object autocommit, read-your-writes inside a scope, all-or-none post-commit visibility, rollback, durable recovery, fault propagation, and transaction lifecycle events. The WAL path includes concurrent group commit, checkpoint-aware bounded recycling, contiguous batched frame writes, segment-order read-ahead, full/delta/no-op checkpoints, dirty-stripe accounting, background checkpoint publication, integrity verification, and bounded copy-forward compaction.

The implementation is packaged in `InterfaceDB.Storage.SegmentCore` and is wired through InterfaceDB's `IRepository<TContract>` and query/index layers. It is a separate opt-in provider; the file-per-object provider does not inherit SegmentCore transaction semantics.

Research and measurement date: 2026-08-25.

## Public Prototype API

An explicit transaction owns an in-memory write set until commit:

```csharp
await using var store = await SegmentCoreStore.OpenAsync(root, options);
await using var transaction = await store.BeginTransactionAsync();

await transaction.SaveAsync("student/42", studentPayload);
await transaction.SaveAsync("audit/42", auditPayload);
await transaction.DeleteAsync("pending/42");

// The transaction reads its staged value; the store still exposes the old state.
var staged = await transaction.GetAsync("student/42");

await transaction.CommitAsync();
```

Upper storage layers can pass the provider-neutral `IStorageTransaction` handle back to SegmentCore:

```csharp
IStorageTransaction? transaction = await store.BeginTransactionAsync();
await store.SaveAsync("student/42", studentPayload, transaction);
await store.DeleteAsync("pending/42", transaction);
await transaction.CommitAsync();
```

Passing `null` selects the existing implicit autocommit behavior. A handle from another store is rejected. Disposing an active scope rolls it back. Transaction events report state transitions for observability; event handlers do not control or change the durable outcome.

For a complete physical-order scan, use the read-ahead path rather than issuing one point read per key:

```csharp
foreach (var record in await store.ReadAllAsync())
{
    Process(record.Key, record.Payload);
}
```

The compaction API is available only to mutation-WAL stores retaining at least two checkpoint generations. It uses bounded preparation and copy slices by default:

```csharp
SegmentCoreCompactionResult result = await store.CompactAsync(
    new SegmentCoreCompactionOptions
    {
        MaximumRecordsPerSlice = 64,
        MaximumStripesPerPreparationSlice = 1
    });
Console.WriteLine($"Reclaimed {result.BytesReclaimed:N0} bytes; max pause {result.LongestExclusiveSlice.TotalMilliseconds:N2} ms");
```

## Durability Protocol

The coordinator sits above physical routing:

```text
upper storage/repository layer
    -> explicit handle or implicit one-object autocommit
    -> SegmentCore transaction coordinator
    -> current hash-striped participant routing
    -> checksummed data frames
    -> durable coordinator commit record
    -> atomic primary-index publication
```

The original `ParticipantCommit` protocol performs these steps:

1. Freeze the transaction's final per-key mutation set.
2. Enter a store-level commit barrier so an explicit transaction cannot straddle a checkpoint or interleave with an implicit mutation.
3. Write each mutation as a normal direct-readable put/delete frame carrying a nonzero 64-bit transaction ID.
4. Flush all participating stripes; participants can flush in parallel.
5. Append and durably acknowledge one checksummed coordinator record containing transaction ID, mutation count, participant count, and a checksum of the exact mutation-address set.
6. Publish every primary locator/tombstone while holding one visibility write lock.

Existing `SaveAsync` and `DeleteAsync` calls use transaction ID zero. Their single durable frame is self-committing, so they do not pay for a second coordinator write.

The new `MutationWriteAheadLog` protocol keeps ordinary transaction-ID-zero saves unchanged, but changes an explicit commit:

1. Append direct-readable derived frames to their stripes without issuing explicit per-stripe flush calls. With a write-through stripe handle, the operating system still acknowledges each write under that policy.
2. Queue the complete mutation set, payloads, checksums, and original prepared locators as one WAL record. Concurrent transactions retain preparation order but need not wait for the preceding transaction's durability acknowledgement before entering the commit queue.
3. The single WAL worker combines bounded queued records into contiguous writes and performs one durability flush for the group. Each transaction is acknowledged only after the flush covering its own complete record. It is then a recovery authority independent of the other grouped transactions.
4. Publish all locators/tombstones under one visibility lock.

If a crash occurs before the WAL commit, prepared frames are ignored. If the WAL is durable but any derived frame is missing, recovery validates the surviving frames and reconstructs the missing frame from the WAL payload. Original prepared locators retained by the WAL preserve per-key logical ordering during that reconstruction. A checkpoint flushes dirty stripes in parallel before publishing its transaction watermark, so it never suppresses WAL replay for data that is not yet durable.

After checkpoint publication, WAL recycling can create a checksummed replacement with a durable base transaction watermark and atomically replace the old coordinator file. Recycling advances only through the oldest retained recovery root, not automatically through the newest delta. To avoid a durable file replacement after every small checkpoint, physical recycling is skipped until reclaimable bytes reach `WalRecycleMinimumReclaimBytes` (4 MiB by default), unless the WAL reaches `WalMaximumBytesBeforeForcedRecycle` (64 MiB by default). A retained sub-threshold WAL can therefore add reopen scan time, but it does not cause tail replay or weaken recovery. Failures before or after an attempted replacement fault the open store; reopen safely chooses the old complete WAL or the new rebased WAL.

Select the mode when creating the store:

```csharp
var options = new SegmentCoreOptions
{
    TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog
};
```

The choice is persisted in `transaction-protocol.manifest`; reopening an existing store with another protocol is rejected. WAL records are checksummed, aligned, tail-repaired, and limited to 256 MiB / 10,000,000 mutations per transaction by the current prototype format.

If storage fails after preparation starts, the transaction becomes faulted and the store rejects further use until reopen. This is intentional: a failed flush can leave an uncertain commit outcome, and recovery is the only safe authority. Reopen produces either the complete transaction or none of it.

## Isolation And Scope Boundaries

The current contract is deliberately narrower than a relational transaction manager:

- atomicity and durability cover one SegmentCore store root;
- one writer process owns that root;
- point reads use read-committed visibility and a transaction reads its own staged writes;
- separate reads are not a repeatable-read or snapshot transaction;
- the write set is buffered in memory until commit;
- nested, ambient, cross-provider, distributed, and multi-process transactions are not implemented;
- explicit commit and checkpoint capture use the same mutation barrier, but checkpoint file publication and WAL recycling continue after writers are released;
- the mutation WAL is checkpoint-recycled; the participant coordinator journal is still append-only;
- provider notifications are emitted only after a durable commit.

These boundaries must remain visible in the eventual provider capability contract.

## Correctness Evidence

The managed suite covers:

- invisibility before commit and read-your-writes;
- atomic put/delete publication;
- explicit rollback and rollback-on-dispose;
- full-log recovery of a committed mutation set;
- validated transaction replay after a primary-index checkpoint;
- abandoned prepared frames without a commit record;
- commit-journal flush uncertainty and mandatory reopen;
- same-key locator ordering, concurrent implicit writes, checkpoint fallback, segment rotation, and tail repair inherited from the existing SegmentCore tests.

The complete SegmentCore suite has 93 tests on Windows and Linux. Coverage includes shared-flush concurrent commits, immutable protocol selection, whole-set reopen, same-key implicit/explicit ordering, abandoned-prepare handling, checkpoint-tail replay, injected flush uncertainty, reconstruction after every derived segment is truncated back to its header, bounded recycling, retained-WAL seek validation/fallback, resident/paged locator fallback, no-op/full/delta fallback, missing-coordinator rejection, failures injected on both sides of atomic WAL replacement, synchronous and asynchronous point reads, Unicode key validation, physical-order and bounded-streaming reads, fresh-segment combined-batch rotation, writes continuing during checkpoint publication, bounded compaction, integrity verification, cancellation-safe online backup/restore, explicit provider migration, storage-format rejection, public-API baseline enforcement, persisted-index staleness/corruption fallback, standard runtime metrics, and full InterfaceDB provider reopen/query/delete behavior. The benchmark harness also kills a separate process after durable commit and immediately before and after WAL replacement; all three maintained scenarios recovered every committed object.

## Fair Performance Result

The maintained comparison used .NET 10 on the Windows development machine, 5,000 complete `GateRecord` objects, five fresh-store iterations, SegmentCore with eight hash stripes and explicit-flush durability, and SQLite through `Microsoft.Data.Sqlite` in WAL/`synchronous=FULL` mode.

Both paths include object encoding or six-field binding and return only after durable commit acknowledgement. Each explicit batch is one real atomic transaction on both engines. Maintenance is measured separately and symmetrically: SegmentCore publishes an index checkpoint and recycles its WAL, while SQLite checkpoints/truncates its WAL. The `+ checkpoint` throughput below amortizes one maintenance checkpoint after every 5,000 records; a production interval can be substantially larger.

| Batch | WAL records/s | SQLite records/s | Raw result | WAL + checkpoint records/s | SQLite + checkpoint records/s | Amortized result | WAL checkpoint | WAL reopen |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 296 | 292 | 1.01x faster | 296 | 292 | 1.01x faster | 19.65 ms | 12.46 ms |
| 10 | 2,930 | 2,920 | 1.00x faster | 2,904 | 2,907 | effectively tied | 15.40 ms | 9.67 ms |
| 100 | 27,553 | 26,191 | 1.05x faster | 25,145 | 25,238 | effectively tied | 17.38 ms | 9.07 ms |
| 1,000 | 166,914 | 137,569 | 1.21x faster | 98,420 | 116,250 | 1.18x slower | 20.85 ms | 9.78 ms |
| Independent autocommit | 301 | 297 | 1.01x faster | 300 | 297 | 1.01x faster | 7.63 ms | 5.75 ms |

The persisted seek boundary changes reopen and checkpoint maintenance, not the timed commit path. Portable explicit-flush throughput remains approximately SQLite-class; checkpoint policy can decide the amortized winner.

The independent row above uses the same `ExplicitFlush` SegmentCore configuration selected for the atomic-batch experiment. It is not the qualified native-Windows write-through configuration below.

## Native Windows Write-Through Profile

The same maintained transaction workload was also run with SegmentCore's qualified native-Windows `WriteThrough` strategy. This is not interchangeable with the explicit-flush table: every derived frame uses a write-through handle, which strongly favors small transactions on this machine but cannot amortize large batches as effectively.

| Batch | WAL records/s | SQLite records/s | Raw result | WAL + checkpoint records/s | SQLite + checkpoint records/s | Amortized result | WAL checkpoint | WAL reopen |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 2,607 | 301 | 8.67x faster | 2,589 | 301 | 8.61x faster | 13.34 ms | 13.04 ms |
| 10 | 15,885 | 2,900 | 5.48x faster | 15,412 | 2,887 | 5.34x faster | 9.68 ms | 7.88 ms |
| 100 | 99,910 | 24,198 | 4.13x faster | 82,106 | 23,392 | 3.51x faster | 10.85 ms | 9.41 ms |
| 1,000 | 303,783 | 136,790 | 2.22x faster | 161,536 | 114,975 | 1.40x faster | 14.49 ms | 8.90 ms |
| Independent autocommit | 4,368 | 300 | 14.54x faster | 4,234 | 300 | 14.11x faster | 7.24 ms | 4.99 ms |

On this Windows/NVMe system, write-through leads SQLite before and after maintenance at every tested batch and reached 303,783 records/s at batch 1,000. Explicit flush reached 166,914 records/s at that batch. The durability strategy remains immutable for an existing store. An actual power cut is still required for the strongest durability claim; bare-metal Linux/NVMe measurements are lower-priority characterization before publishing results for that profile.

Thresholded recycling keeps physical checkpoint maintenance bounded. A checksummed, checkpoint-bound seek sidecar skips to a retained WAL boundary only after the covered prefix is fully SHA-256 verified; corrupt or missing sidecars fall back to complete scanning. SegmentCore's latest rows reopened the provider in 4.99-13.04 ms versus opening SQLite in 0.36-0.55 ms. Those timings stop before the first application object is materialized, so they compare fixed provider-open cost rather than object retrieval. SQLite still has the lower fixed open cost. An O(1) trust-the-boundary shortcut remains deliberately unimplemented because it would weaken that full-prefix integrity guarantee.

The segment-order cursor groups live locators into bounded 4 MiB windows, reads independent segments in parallel, validates every frame, and decodes complete payloads into `GateRecord` objects. SQLite likewise maps all six columns into `GateRecord` objects. The maintained 5,000-object scans took 6.02-15.65 ms with explicit flush and 6.64-17.02 ms with write-through, versus SQLite's 6.80-8.04 ms and 6.76-7.89 ms. Each engine led five of the ten operation/durability rows. Remaining differences depend on physical fragmentation and access order.

## Incremental Checkpoints And Compaction

Checkpoint generations now alternate self-contained full snapshots with checksummed deltas. A delta stores only changed locators and tombstones, but pruning retains two independent full recovery roots rather than allowing every retained delta to depend on one corruptible base. In the 5,000-object maintained run, a full checkpoint file was 288.4 KiB and a 1%-dirty delta was 3.2 KiB, about a 90x reduction in checkpoint write amplification.

The latest maintenance run measured a 16.01 ms first full checkpoint under explicit flush, a 0.69 ms unchanged checkpoint that reused generation 1, a 19.71 ms retained full fallback, and a 24.19 ms 1%-dirty delta. Write-through measured 9.19 ms, 0.76 ms, 13.83 ms, and 17.82 ms respectively. The tiny delta closes metadata write amplification; stripe durability barriers, atomic publication, WAL hashing, and page-map policy still determine latency.

`CompactAsync` is an interruption-safe, WAL-only bounded copy-forward compactor. It performs free-space admission, rotates one stripe per short preparation slice by default, copies 64 live records per slice, publishes two independent full checkpoints after releasing writers, briefly retires readers, and only then deletes superseded segments. With two update generations over 5,000 live objects the latest case reclaimed 2,154.9 KiB, reducing total store size from 6,740.4 KiB to 4,585.5 KiB. Total compaction was 178.25 ms under explicit flush and 148.84 ms under write-through across 89 slices; the longest exclusive slices were 14.88 ms and 8.79 ms.

Final local reports:

- `segment-core-transactions-release-optimized-explicit.json`
- `segment-core-transactions-release-optimized-write-through.json`

Reproduce:

```powershell
dotnet run -c Release --project StorageBenchmarks.Transactions -- `
  --segment-core-transactions `
  --records=5000 `
  --independent-records=1000 `
  --iterations=5 `
  --batch-sizes=1,10,100,1000 `
  --stripes=8 `
  --protocols=both
```

Use `--durability=write-through` to reproduce the native Windows profile.

## What The Routing Controls Showed

The same transaction benchmark was rerun with a fixed one-, two-, four-, and eight-stripe SegmentCore store to isolate participant locality:

| Batch | 1 stripe | 2 stripes | 4 stripes | 8 stripes |
|---:|---:|---:|---:|---:|
| 10 | 1,502/s | 1,381/s | 1,289/s | 1,158/s |
| 100 | 9,208/s | 8,749/s | 7,764/s | 7,611/s |
| 1,000 | 38,114/s | 43,254/s | 43,896/s | 52,607/s |

Fewer participants improve small and medium transactions by avoiding durability barriers. More stripes win for large transactions because parallel frame writes outweigh the extra participant flushes. Routing everything to one bucket is therefore not a general solution.

## Recommendation And Product Boundary

Keep participant commit as the independent correctness baseline and mutation WAL as the default production-provider protocol. Group commit, provider integration, persisted secondary-index catalogs, dirty-stripe/no-op/delta checkpointing, retained-WAL seek boundaries, paged locators, background publication, parallel reopen, bounded streaming, integrity, automatic maintenance, bounded/free-space-admitted compaction, provider migration, online backup/restore, and compatibility enforcement are complete. The maintained results support “SQLite-class portable explicit-flush transaction throughput” and “strong qualified Windows/NVMe write-through throughput,” not a blanket claim that SegmentCore replaces SQLite. The full virtualized-Linux Phase 0 run passed correctness and 12/12 crash cycles but achieved only a 5.4% p50 lead over SQLite, below its 15% admission rule. Remaining release evidence is deliberately narrower:

1. Execute a real physical power cut; Docker, process termination, fault injection, and the two-boot graceful rehearsal are evidence but not substitutes.
2. Decide whether the now-measured one-owning-process boundary remains explicit or justifies a separately designed read-only snapshot/lease path. SQLite supports the concurrent reader case today; SegmentCore does not.
3. Decide whether repository envelope skeletons also need an on-disk/lazy representation.
4. Decide the next package version and whether the known virtualized-Linux performance miss is accepted for the first stable release.
5. Treat bare-metal Linux/NVMe and true cold-media runs as lower-priority, hardware-profile-specific characterization.
